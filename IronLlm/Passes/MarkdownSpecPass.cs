using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using IronLlm.Graph;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

// Runs only when the input file has a .md extension.
// Sends the Markdown document to the LLM and extracts a .spec file from the response.
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

        var artifactPath = Path.Combine(context.ArtifactsDir, ArtifactFile!);
        Directory.CreateDirectory(context.ArtifactsDir);
        await File.WriteAllTextAsync(artifactPath, extracted);

        context.SpecPath = artifactPath;
        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms → {Artifact}",
            Name, sw.ElapsedMilliseconds, artifactPath);
    }

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        // The artifact IS the extracted .spec — just redirect SpecPath.
        if (File.Exists(artifactPath))
        {
            context.SpecPath = artifactPath;
            _logger.LogDebug("Loaded extracted spec from {Path}", artifactPath);
        }
        await Task.CompletedTask;
    }

    private async Task<string> ExtractSpecAsync(string markdown)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, markdown),
        };

        var response = await _chat.GetResponseAsync(messages);
        return response.Text?.Trim() ?? "";
    }

    // Runs the SemanticGraphPass parser against the extracted text to confirm it
    // produces a non-trivial graph before we accept the LLM output.
    private static void ValidateExtracted(string specText, string sourcePath)
    {
        var tempCtx = new CompilationContext
        {
            SpecPath     = sourcePath,
            ArtifactsDir = Path.GetTempPath(),
            RawSpec      = specText,
        };

        // Use NullLogger — validation errors surface as exceptions, not log messages.
        var graphPass = new SemanticGraphPass(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SemanticGraphPass>.Instance);
        graphPass.ExecuteAsync(tempCtx).GetAwaiter().GetResult();

        var graph = tempCtx.SemanticGraph;
        if (graph == null || graph.Nodes.Count == 0)
            throw new CompilationException(
                "LLM extraction produced an empty or invalid .spec (no graph nodes).");

        if (!graph.Nodes.OfType<ProgramNode>().Any())
            throw new CompilationException(
                "LLM extraction produced a .spec with no program: declaration.");
    }

    private const string SystemPrompt = """
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
            type: <type>

        Rules:
        1. Output ONLY the .spec content — no explanation, no markdown fences.
        2. Use snake_case for all condition names.
        3. Include a "default" branch (no divisor) for the fallback output.
        4. The variable block must name the loop counter.
        5. If the document describes a program that cannot be expressed in this format,
           output a single line: ERROR: <reason>
        """;
}
