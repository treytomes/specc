using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

public class ParseSpecPass : ICompilerPass
{
    private readonly ILogger<ParseSpecPass> _logger;

    public ParseSpecPass(ILogger<ParseSpecPass> logger)
    {
        _logger = logger;
    }

    public string Name         => "01-ParseSpec";
    public string? ArtifactFile => "01-spec.json";

    public Task ExecuteAsync(CompilationContext context)
    {
        var sw = Stopwatch.StartNew();
        context.RawSpec = File.ReadAllText(context.SpecPath);
        _logger.LogDebug("Spec: {Path} ({Chars} chars)", context.SpecPath, context.RawSpec.Length);
        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
        return Task.CompletedTask;
    }

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(artifactPath));
        context.RawSpec = doc.RootElement.GetProperty("raw").GetString();
    }
}
