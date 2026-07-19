using Microsoft.Extensions.Logging;

namespace Specc.Passes;

/// <summary>Runs the ordered list of compiler passes, skipping any whose artifact already exists.</summary>
public class CompilationPipeline
{
    private readonly IEnumerable<ICompilerPass> _passes;
    private readonly ILogger<CompilationPipeline> _logger;

    /// <summary>Initialises the pipeline with an ordered pass list and a logger.</summary>
    public CompilationPipeline(IEnumerable<ICompilerPass> passes, ILogger<CompilationPipeline> logger)
    {
        _passes = passes;
        _logger = logger;
    }

    /// <summary>Executes all passes in order, loading from disk when an artifact exists.</summary>
    public async Task RunAsync(CompilationContext context)
    {
        foreach (var pass in _passes)
        {
            var artifactPath = pass.ArtifactFile is { } f
                ? Path.Combine(context.ArtifactsDir, f)
                : null;

            if (artifactPath != null && File.Exists(artifactPath))
            {
                _logger.LogInformation("Skipped {Name} (artifact exists)", pass.Name);
                await pass.LoadFromArtifactAsync(artifactPath, context);
                continue;
            }

            _logger.LogInformation("Running pass {Name}", pass.Name);
            try
            {
                await pass.ExecuteAsync(context);
            }
            catch (CompilationException ex)
            {
                _logger.LogError("Pass {Name} failed: {Message}", pass.Name, ex.Message);
                throw;
            }
            await ArtifactWriter.WritePassArtifactAsync(pass, context, _logger);
        }

        await ManifestWriter.WriteAsync(context);

        _logger.LogInformation("Compilation complete");

        if (context.LauncherPath != null)
            _logger.LogInformation("Executable: {Path}", context.LauncherPath);
        else if (context.AssemblyPath != null)
            _logger.LogInformation("Assembly: {Path}", context.AssemblyPath);
    }
}
