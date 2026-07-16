using System.Text.Json;

namespace IronLlm.Passes;

public class ParseSpecPass : ICompilerPass
{
    public string Name         => "01-ParseSpec";
    public string? ArtifactFile => "01-spec.json";

    public Task ExecuteAsync(CompilationContext context)
    {
        context.RawSpec = File.ReadAllText(context.SpecPath);
        return Task.CompletedTask;
    }

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(artifactPath));
        context.RawSpec = doc.RootElement.GetProperty("raw").GetString();
    }
}
