using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

/// <summary>Reads the <c>.spec</c> file from disk into <see cref="CompilationContext.RawSpec"/>.</summary>
public class ParseSpecPass : ICompilerPass
{
    private readonly ILogger<ParseSpecPass> _logger;

    /// <summary>Initialises the pass with a logger.</summary>
    public ParseSpecPass(ILogger<ParseSpecPass> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name         => "01-ParseSpec";
    /// <inheritdoc/>
    public string? ArtifactFile => "01-spec.json";

    /// <inheritdoc/>
    public Task ExecuteAsync(CompilationContext context)
    {
        var sw = Stopwatch.StartNew();
        context.RawSpec = File.ReadAllText(context.SpecPath);
        _logger.LogDebug("Spec: {Path} ({Chars} chars)", context.SpecPath, context.RawSpec.Length);
        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(artifactPath));
        context.RawSpec = doc.RootElement.GetProperty("raw").GetString();
    }
}
