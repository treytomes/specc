using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

public class CompilationPipeline
{
    private readonly IEnumerable<ICompilerPass> _passes;
    private readonly ILogger<CompilationPipeline> _logger;

    public CompilationPipeline(IEnumerable<ICompilerPass> passes, ILogger<CompilationPipeline> logger)
    {
        _passes = passes;
        _logger = logger;
    }

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
            await pass.ExecuteAsync(context);
            await ArtifactWriter.WritePassArtifactAsync(pass, context, _logger);
        }

        _logger.LogInformation("Compilation complete");

        if (context.LauncherPath != null)
            _logger.LogInformation("Executable: {Path}", context.LauncherPath);
        else if (context.AssemblyPath != null)
            _logger.LogInformation("Assembly: {Path}", context.AssemblyPath);
    }
}
