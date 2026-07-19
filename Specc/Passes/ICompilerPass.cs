namespace Specc.Passes;

public interface ICompilerPass
{
    string Name { get; }

    // Artifact filename this pass owns, e.g. "04-cfg.json".
    // Null means the pass produces no artifact (or always re-runs).
    string? ArtifactFile { get; }

    Task ExecuteAsync(CompilationContext context);

    // Populate context from a previously-written artifact instead of re-running.
    // Only called when ArtifactFile exists on disk and is up-to-date.
    Task LoadFromArtifactAsync(string artifactPath, CompilationContext context);
}
