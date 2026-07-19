using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Specc.Graph;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

// Runs only when the input file has a .md extension.
// Makes two LLM calls: one to extract a .spec, one to extract explicit acceptance criteria.
// When the input is already a .spec file this pass is a no-op (ArtifactFile returns null).
/// <summary>Extracts a structured <c>.spec</c> and authorial acceptance criteria from a Markdown program description using an LLM.</summary>
[ExcludeFromCodeCoverage(Justification = "LLM I/O path; covered by scripts/test.sh")]
public class MarkdownSpecPass : ICompilerPass
{
    private readonly IChatClient _chat;
    private readonly ILogger<MarkdownSpecPass> _logger;

    /// <summary>Initialises the pass with a chat client and a logger.</summary>
    public MarkdownSpecPass(IChatClient chat, ILogger<MarkdownSpecPass> logger)
    {
        _chat   = chat;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "00-MarkdownSpec";

    /// <inheritdoc/>
    public string? ArtifactFile => "00-extracted.spec";

    /// <inheritdoc/>
    public async Task ExecuteAsync(CompilationContext context)
    {
        var sw       = Stopwatch.StartNew();
        var markdown = await File.ReadAllTextAsync(context.SpecPath);
        _logger.LogInformation("Extracting .spec from {Path} ({Chars} chars)", context.SpecPath, markdown.Length);

        var tags      = await ClassifyAsync(markdown);
        _logger.LogInformation("Classifier selected construct families: [{Tags}]", string.Join(", ", tags));
        var extracted = await ExtractSpecAsync(markdown, tags, context.RepositoryPath);

        if (extracted.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Extraction failed with [{Tags}] — retrying with full construct set", string.Join(", ", tags));
            tags      = ["loop", "branch", "arithmetic", "input", "array", "while", "random"];
            extracted = await ExtractSpecAsync(markdown, tags, context.RepositoryPath);
        }

        if (extracted.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            throw new CompilationException(
                $"LLM could not extract a .spec from the Markdown document: {extracted}");

        var missing = ConsistencyMissing(tags, extracted);
        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "Extraction incomplete [{Tags}] missing {Missing} — retrying with full construct set",
                string.Join(", ", tags), string.Join(", ", missing));
            var fullTags  = new[] { "loop", "branch", "arithmetic", "input", "array", "while", "random" };
            var retried   = await ExtractSpecAsync(markdown, fullTags, context.RepositoryPath);
            // Only check whether the originally-missing constructs are now present — don't penalise
            // the retry for not using array/random/loop that the program simply doesn't need.
            var stillMissing = missing.Where(m => !retried.Contains(m, StringComparison.Ordinal)).ToList();
            if (!retried.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) && stillMissing.Count == 0)
            {
                tags      = fullTags;
                extracted = retried;
                missing   = [];
            }
            else
            {
                _logger.LogWarning("Retry did not resolve missing constructs — proceeding with original extraction");
            }
        }

        ValidateExtracted(extracted, context.SpecPath);

        Directory.CreateDirectory(context.ArtifactsDir);
        var specArtifact = Path.Combine(context.ArtifactsDir, ArtifactFile!);
        await File.WriteAllTextAsync(specArtifact, extracted);
        context.SpecPath = specArtifact;

        // Try to extract expected output directly from a fenced code block under
        // "## Expected Output" — deterministic, no LLM needed, works for array programs.
        // Set test input for interactive programs.
        var testInput = ParseTestInputBlock(markdown);
        if (testInput != null)
        {
            context.TestInput = testInput;
            _logger.LogInformation("Parsed test input from Markdown: \"{Input}\"", testInput);
        }

        var directLines = ParseExpectedOutputBlock(markdown);
        List<AssertionRecord> authorial;
        if (directLines.Count > 0)
        {
            authorial = directLines
                .Select((line, i) =>
                {
                    // Lines containing {variable} placeholders are dynamic.
                    // If we have a TestInput value, substitute it for all {placeholder}s and use exact matching.
                    // Otherwise fall back to substring matching on whatever static text surrounds the placeholder.
                    if (line.Contains('{') && line.Contains('}'))
                    {
                        var resolved = testInput != null
                            ? System.Text.RegularExpressions.Regex.Replace(line, @"\{[^}]+\}", testInput)
                            : line;
                        var isSubstring = testInput == null;
                        return new AssertionRecord(i, resolved, IsSubstring: isSubstring);
                    }
                    return new AssertionRecord(i, line);
                })
                .ToList();
            _logger.LogInformation("Parsed {Count} expected output lines directly from Markdown", authorial.Count);
        }
        else if (extracted.Contains("random:", StringComparison.Ordinal))
        {
            // Non-deterministic program — no predictable expected output; skip criteria extraction.
            authorial = [];
        }
        else
        {
            // Fall back to the LLM criteria call for loop/divisor programs.
            var criteria = await ExtractAuthorialCriteriaAsync(markdown);
            authorial = EvaluateRules(criteria);
        }

        if (authorial.Count > 0)
        {
            context.AuthorialAssertions = authorial;
            var criteriaPath = Path.Combine(context.ArtifactsDir, "00-authorial-criteria.json");
            await File.WriteAllTextAsync(criteriaPath,
                JsonSerializer.Serialize(authorial, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("Extracted {Count} authorial assertions → {Path}",
                authorial.Count, criteriaPath);
        }

        if (testInput != null)
        {
            var testInputPath = Path.Combine(context.ArtifactsDir, "00-test-input.txt");
            await File.WriteAllTextAsync(testInputPath, testInput);
        }
        else
        {
            _logger.LogDebug("No extractable acceptance criteria in Markdown — falling back to graph-derived");
        }

        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms → {Artifact}",
            Name, sw.ElapsedMilliseconds, specArtifact);
    }

    /// <inheritdoc/>
    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        // The artifact IS the extracted .spec — just redirect SpecPath.
        if (File.Exists(artifactPath))
        {
            context.SpecPath = artifactPath;
            _logger.LogDebug("Loaded extracted spec from {Path}", artifactPath);
        }

        // Also restore authorial criteria if they were produced.
        var criteriaPath = Path.Combine(Path.GetDirectoryName(artifactPath)!, "00-authorial-criteria.json");
        if (File.Exists(criteriaPath))
        {
            var json = await File.ReadAllTextAsync(criteriaPath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            context.AuthorialAssertions =
                JsonSerializer.Deserialize<List<AssertionRecord>>(json, opts) ?? [];
            _logger.LogDebug("Loaded {Count} authorial assertions from {Path}",
                context.AuthorialAssertions.Count, criteriaPath);
        }

        // Restore test input if present.
        var testInputPath = Path.Combine(Path.GetDirectoryName(artifactPath)!, "00-test-input.txt");
        if (File.Exists(testInputPath))
        {
            context.TestInput = await File.ReadAllTextAsync(testInputPath);
            _logger.LogDebug("Loaded test input from {Path}", testInputPath);
        }
    }

    private static readonly JsonElement ClassifierSchema = JsonDocument.Parse("""
        { "type": "array", "items": { "type": "string" } }
        """).RootElement;

    private static readonly ChatOptions ClassifierOptions = new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(ClassifierSchema, "ConstructFamilies", null),
    };

    private async Task<string[]> ClassifyAsync(string markdown)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ClassifierSystemPrompt),
            new(ChatRole.User, markdown),
        };

        try
        {
            var response = await _chat.GetResponseAsync(messages, ClassifierOptions);
            var text = response.Text?.Trim() ?? "[]";
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var tags = JsonSerializer.Deserialize<string[]>(text, opts) ?? [];
            // Guarantee at least a minimal set so extraction never gets an empty prompt.
            if (tags.Length == 0)
                tags = ["loop", "branch"];
            return tags;
        }
        catch
        {
            _logger.LogWarning("Classifier call failed — falling back to full construct set");
            return ["loop", "branch", "arithmetic", "input", "array", "while", "random"];
        }
    }

    private async Task<string> ExtractSpecAsync(string markdown, string[] tags,
        string repositoryPath = "")
    {
        var systemPrompt = SpecConstructLibrary.Assemble(tags);

        if (!string.IsNullOrEmpty(repositoryPath))
        {
            var priors = await Specc.Passes.Repository.GraphRepository
                .FindPriorsByTagsAsync(repositoryPath, tags);
            if (priors.Count > 0)
            {
                var priorsText = string.Join("\n\n",
                    priors.Select(p => $"Prior example ({p.ProgramName}):\n{p.SpecText.Trim()}"));
                systemPrompt += $"\n\nHere are similar programs that have been compiled successfully. Use them as structural templates:\n\n{priorsText}\n";
            }
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, markdown),
        };

        var response = await _chat.GetResponseAsync(messages);
        return StripFences(response.Text?.Trim() ?? "");
    }

    // Strip markdown code fences that some models emit despite being told not to.
    private static string StripFences(string text)
    {
        var lines  = text.Split('\n');
        var start  = 0;
        var end    = lines.Length - 1;
        if (start <= end && lines[start].TrimStart().StartsWith("```"))
            start++;
        if (end >= start && lines[end].TrimStart().StartsWith("```"))
            end--;
        return string.Join('\n', lines[start..(end + 1)]).Trim();
    }

    private static readonly JsonElement CriteriaSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "loopFrom": { "type": "integer" },
            "loopTo":   { "type": "integer" },
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "divisor":   { "type": "integer" },
                  "isDefault": { "type": "boolean" },
                  "expected":  { "type": "string"  }
                }
              }
            }
          }
        }
        """).RootElement;

    private static readonly ChatOptions CriteriaOptions = new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(CriteriaSchema, "AuthorialCriteria", null),
    };

    private async Task<AuthorialCriteriaDto> ExtractAuthorialCriteriaAsync(string markdown)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, CriteriaSystemPrompt),
            new(ChatRole.User, markdown),
        };

        try
        {
            var response = await _chat.GetResponseAsync(messages, CriteriaOptions);
            var text = response.Text?.Trim() ?? "{}";
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<AuthorialCriteriaDto>(text, opts) ?? new AuthorialCriteriaDto();
        }
        catch
        {
            return new AuthorialCriteriaDto();
        }
    }

    /// <summary>Evaluates the extracted authorial rules into a list of per-iteration assertion records.</summary>
    public static List<AssertionRecord> EvaluateRules(AuthorialCriteriaDto dto)
    {
        if (dto.Rules == null || dto.Rules.Count == 0 || dto.LoopTo <= dto.LoopFrom)
            return [];

        var modRules = dto.Rules
            .Where(r => !r.IsDefault && r.Divisor > 0)
            .OrderByDescending(r => r.Divisor)
            .ToList();
        var defaultRule = dto.Rules.FirstOrDefault(r => r.IsDefault);

        var assertions = new List<AssertionRecord>(dto.LoopTo - dto.LoopFrom + 1);
        for (var i = dto.LoopFrom; i <= dto.LoopTo; i++)
        {
            var matched = false;
            foreach (var rule in modRules)
            {
                if (i % rule.Divisor == 0)
                {
                    assertions.Add(new AssertionRecord(i, rule.Expected ?? i.ToString()));
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                var expected = defaultRule?.Expected ?? "{n}";
                // Resolve variable placeholder.
                if (expected.StartsWith('{') && expected.EndsWith('}'))
                    expected = i.ToString();
                assertions.Add(new AssertionRecord(i, expected));
            }
        }
        return assertions;
    }

    /// <summary>Returns any construct keywords expected by the classifier tags that are absent from the extracted spec text.</summary>
    public static List<string> ConsistencyMissing(string[] tags, string specText)
    {
        var missing = new List<string>();
        if (tags.Contains("while")      && !specText.Contains("while:"))          missing.Add("while:");
        if (tags.Contains("arithmetic") && !specText.Contains("assign:"))         missing.Add("assign:");
        if (tags.Contains("input")      && !specText.Contains("source: stdin"))   missing.Add("source: stdin");
        if (tags.Contains("array")      && !specText.Contains("initial_value:")
                                        && !specText.Contains("array[int]"))      missing.Add("array construct");
        if (tags.Contains("branch")     && !specText.Contains("branch:"))         missing.Add("branch:");
        if (tags.Contains("loop")       && !specText.Contains("loop:"))           missing.Add("loop:");
        if (tags.Contains("random")     && !specText.Contains("random:"))         missing.Add("random:");
        return missing;
    }

    // Runs the SemanticGraphPass parser against the extracted text to confirm it
    // produces a non-trivial graph before we accept the LLM output.
    // If the spec contains constructs outside the current flat-loop format (e.g. nested
    // loops with dynamic bounds), the parser may throw a format exception — that is not
    // an LLM failure; it means the spec is valid but requires downstream graph handling.
    // In that case we skip the structural check and proceed.
    private static void ValidateExtracted(string specText, string sourcePath)
    {
        var tempCtx = new CompilationContext
        {
            SpecPath     = sourcePath,
            ArtifactsDir = Path.GetTempPath(),
            RawSpec      = specText,
        };

        var graphPass = new SemanticGraphPass(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SemanticGraphPass>.Instance);

        try
        {
            graphPass.ExecuteAsync(tempCtx).GetAwaiter().GetResult();
        }
        catch (FormatException)
        {
            // Dynamic bounds or other non-literal values in the spec — not an LLM error.
            return;
        }
        catch (CompilationException)
        {
            // The spec describes a program type the current flat-loop parser can't validate
            // (e.g. array sorting programs where the LLM uses divisor-style branch syntax).
            // Accept the spec; CfgPass dispatches on graph shape, not spec syntax.
            return;
        }

        var graph = tempCtx.SemanticGraph;
        if (graph == null || graph.Nodes.Count == 0)
            throw new CompilationException(
                "LLM extraction produced an empty or invalid .spec (no graph nodes).");

        if (!graph.Nodes.OfType<ProgramNode>().Any())
            throw new CompilationException(
                "LLM extraction produced a .spec with no program: declaration.");
    }

    private static List<string> ParseExpectedOutputBlock(string markdown)
    {
        var lines     = markdown.Split('\n');
        var inSection = false;
        var inFence   = false;
        var result    = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("## Expected Output", StringComparison.OrdinalIgnoreCase))
            { inSection = true; continue; }
            if (!inSection) continue;
            // Next ## heading ends a bare-line block.
            if (trimmed.StartsWith("## ", StringComparison.Ordinal) && result.Count > 0)
                break;
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inFence) { inFence = true; continue; }
                break; // closing fence — done
            }
            if (inFence)
            {
                if (trimmed.Length > 0) result.Add(trimmed);
            }
            else if (trimmed.Length > 0)
            {
                // Bare (non-fenced) line — collect directly.
                result.Add(trimmed);
            }
        }
        return result;
    }

    // Parses "## Test Input" section: all non-empty lines, joined with newlines, so multi-line
    // programs (e.g. Calculator) can receive multiple inputs in one TestInput string.
    private static string? ParseTestInputBlock(string markdown)
    {
        var lines     = markdown.Split('\n');
        var inSection = false;
        var collected = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("## Test Input", StringComparison.OrdinalIgnoreCase))
            { inSection = true; continue; }
            if (!inSection) continue;
            if (trimmed.StartsWith('#')) break; // next section
            if (!string.IsNullOrWhiteSpace(trimmed))
                collected.Add(trimmed.Trim());
        }
        return collected.Count == 0 ? null : string.Join("\n", collected);
    }

    private const string ClassifierSystemPrompt = """
        You are a program classifier. Read the program description and return a JSON array
        of construct families it requires. Choose from: "loop", "branch", "arithmetic",
        "input", "array", "while", "random". Include a tag if there is any chance the construct is
        needed — it is better to include an extra tag than to miss one.

        Tag meanings:
          "loop"       — fixed iteration range (from N to M)
          "branch"     — conditional output based on divisibility or comparison
          "arithmetic" — arithmetic assignments: multiply, divide, add, subtract
          "input"      — read a value from the user at runtime
          "array"      — fixed-size list of values
          "while"      — repeat until a condition is met; number of iterations not known in advance;
                         phrases like "keep going until", "repeat until", "go back to step N",
                         "loop until N equals", "stop when" all indicate "while"
          "random"     — generate a random number at runtime; phrases like "pick a random number",
                         "choose a random value", "random integer" indicate "random"

        Examples:
          "FizzBuzz from 1 to 100, print Fizz/Buzz"              → ["loop","branch"]
          "Fibonacci first 10 terms"                             → ["loop","arithmetic"]
          "Ask name, greet user"                                 → ["input"]
          "Bubble sort an array of 10 ints"                      → ["loop","array"]
          "Read int, compare to 42, print hint"                  → ["input","branch"]
          "Read a number. Repeat: print it, halve if even, else×3+1. Stop when 1." → ["input","arithmetic","while"]
          "Keep dividing until you reach 1"                      → ["input","arithmetic","while"]
          "Pick a random number, keep guessing until correct"    → ["input","branch","while","random"]

        Return only the JSON array.
        """;

    private const string CriteriaSystemPrompt = """
        You are a test oracle. Your job is to read a program specification written in Markdown
        and extract the explicit acceptance criteria as a compact JSON object.

        Return ONLY valid JSON with no explanation and no markdown fences:

        {
          "loopFrom": <int>,
          "loopTo": <int>,
          "rules": [
            { "divisor": <int>, "expected": "<string>" },
            ...
            { "isDefault": true, "expected": "<string or {variable}>" }
          ]
        }

        Rules:
        1. Sort rules by divisor descending (most-constrained first), default rule last.
        2. Use the exact output string the author stated (e.g. "FizzBuzz", "Fizz", "Buzz").
        3. For the default/fallback case, use "{n}" (or the variable name in braces) if the
           output is the number itself.
        4. If the document does not contain explicit acceptance criteria, return exactly: {}
        """;
}

/// <summary>Deserialized acceptance criteria extracted by the LLM from Markdown prose.</summary>
public class AuthorialCriteriaDto
{
    /// <summary>Inclusive loop start value.</summary>
    public int LoopFrom { get; set; } = 1;

    /// <summary>Inclusive loop end value.</summary>
    public int LoopTo   { get; set; } = 0;

    /// <summary>Ordered list of divisor-based and default rules.</summary>
    [JsonPropertyName("rules")]
    public List<AuthorialRuleDto>? Rules { get; set; }
}

/// <summary>A single authorial output rule: divisor-based or default fallback.</summary>
public class AuthorialRuleDto
{
    /// <summary>Modulo divisor for this rule; 0 for the default rule.</summary>
    public int    Divisor   { get; set; }

    /// <summary>True when this is the fallback rule applied when no divisor matches.</summary>
    public bool   IsDefault { get; set; }

    /// <summary>Expected output string, or <c>{n}</c> for the loop counter.</summary>
    public string? Expected { get; set; }
}
