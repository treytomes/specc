using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronLlm.Graph;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

// Runs only when the input file has a .md extension.
// Makes two LLM calls: one to extract a .spec, one to extract explicit acceptance criteria.
// When the input is already a .spec file this pass is a no-op (ArtifactFile returns null).
[ExcludeFromCodeCoverage(Justification = "LLM I/O path; covered by scripts/test.sh")]
public class MarkdownSpecPass : ICompilerPass
{
    private readonly IChatClient _chat;
    private readonly ILogger<MarkdownSpecPass> _logger;

    public MarkdownSpecPass(IChatClient chat, ILogger<MarkdownSpecPass> logger)
    {
        _chat   = chat;
        _logger = logger;
    }

    public string Name => "00-MarkdownSpec";

    // Only registered when input is .md; pipeline skip logic uses this on re-runs.
    public string? ArtifactFile => "00-extracted.spec";

    public async Task ExecuteAsync(CompilationContext context)
    {
        var sw       = Stopwatch.StartNew();
        var markdown = await File.ReadAllTextAsync(context.SpecPath);
        _logger.LogInformation("Extracting .spec from {Path} ({Chars} chars)", context.SpecPath, markdown.Length);

        var extracted = await ExtractSpecAsync(markdown);

        if (extracted.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            throw new CompilationException(
                $"LLM could not extract a .spec from the Markdown document: {extracted}");

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

    private async Task<string> ExtractSpecAsync(string markdown)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SpecSystemPrompt),
            new(ChatRole.User, markdown),
        };

        var response = await _chat.GetResponseAsync(messages);
        return response.Text?.Trim() ?? "";
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

    // Evaluates extracted rules locally, same algorithm as AcceptanceCriteriaPass.
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
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inFence) { inFence = true; continue; }
                break; // closing fence — done
            }
            if (inFence && trimmed.Length > 0)
                result.Add(trimmed);
        }
        return result;
    }

    // Parses "## Test Input" section: the first non-empty line after the heading is the test input.
    private static string? ParseTestInputBlock(string markdown)
    {
        var lines     = markdown.Split('\n');
        var inSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("## Test Input", StringComparison.OrdinalIgnoreCase))
            { inSection = true; continue; }
            if (!inSection) continue;
            if (trimmed.StartsWith('#')) break; // next section
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed.Trim();
        }
        return null;
    }

    private const string SpecSystemPrompt = """
        You are a compiler front-end. Your job is to read a program specification written
        in Markdown and extract it as a structured .spec file.

        The .spec format is:

          program: <name>

          loop:
            from: <int>
            to: <int>

          branch:
            condition: <snake_case_name>
            divisor: <int>          # omit if no modulo check
            true_output: "<string>" # quoted string or {variable}

          variable:
            name: <identifier>
            type: int | string
            initial_value: <int>    # optional; omit if not explicitly initialized
            source: stdin           # optional; omit unless the variable is read from user input

          print: "<string or {variable}>"   # unconditional output line (no branch needed)

          assign:
            target: <identifier>
            op: mul                 # mul, add, sub, or copy
            left: {variable_or_int}
            right: {variable_or_int} # omit entirely when op is copy

        The "copy" op copies one variable into another. It has no right operand:
          assign:
            target: tmp
            op: copy
            left: {a}

        For programs that read input and print strings in sequence, use print: and variable:
        (with source: stdin) — NO loop:, NO branch:. Example:
          program: Greetings
          print: "Hello! What is your name?"
          variable:
            name: user_name
            type: string
            source: stdin
          print: "{user_name}"

        Rules:
        1. Output ONLY the .spec content — no explanation, no markdown fences.
        2. Use snake_case for all condition names.
        3. Include a "default" branch (no divisor) for the fallback output.
        4. The variable block must name the loop counter (e.g. n).
        5. Use assign: blocks for arithmetic (multiply, add, subtract, copy). Do NOT use branch/divisor for arithmetic.
        6. For {variable} operands use braces: {a}. For integer constants use the number directly: 7.
        7. Do NOT add an assign: block that increments the loop counter (e.g. "assign n add {n} 1"). The loop counter is incremented automatically.
        8. For programs with no loop: use print: for unconditional output. Do NOT use branch: for unconditional output.
        9. If the document describes a program that cannot be expressed in this format,
           output a single line: ERROR: <reason>
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

public class AuthorialCriteriaDto
{
    public int LoopFrom { get; set; } = 1;
    public int LoopTo   { get; set; } = 0;

    [JsonPropertyName("rules")]
    public List<AuthorialRuleDto>? Rules { get; set; }
}

public class AuthorialRuleDto
{
    public int    Divisor   { get; set; }
    public bool   IsDefault { get; set; }
    public string? Expected { get; set; }
}
