namespace Specc.Passes;

/// <summary>Contract implemented by every stage in the compilation pipeline.</summary>
public interface ICompilerPass
{
    /// <summary>Display name used in pipeline logs.</summary>
    string Name { get; }

    /// <summary>Artifact filename this pass owns, e.g. <c>04-cfg.json</c>; null means no artifact.</summary>
    string? ArtifactFile { get; }

    /// <summary>Runs the pass against the given compilation context.</summary>
    Task ExecuteAsync(CompilationContext context);

    /// <summary>Populates context from a previously-written artifact instead of re-running the pass.</summary>
    Task LoadFromArtifactAsync(string artifactPath, CompilationContext context);
}
