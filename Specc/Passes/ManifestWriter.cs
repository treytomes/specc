using System.Security.Cryptography;
using System.Text.Json;

namespace Specc.Passes;

public static class ManifestWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly string[] ArtifactOrder =
    [
        "00-extracted.spec",
        "00-authorial-criteria.json",
        "00-acceptance.json",
        "01-spec.json",
        "02-semantic-graph.json",
        "02b-semantic-graph.mmd",
        "02c-semantic-graph.svg",
        "03-embeddings.json",
        "03b-normalized-graph.json",
        "04-cfg.json",
        "05-stackir.json",
        "06-program.il",
        "07-program.dll",
    ];

    public static async Task WriteAsync(CompilationContext context)
    {
        var specHash = File.Exists(context.SpecPath)
            ? HashFile(context.SpecPath)
            : null;

        var passes = ArtifactOrder
            .Select(name => new { artifact = name, path = Path.Combine(context.ArtifactsDir, name) })
            .Where(a => File.Exists(a.path))
            .Select(a => new { a.artifact, sha256 = HashFile(a.path) })
            .ToList();

        var manifest = new { specHash, passes };
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        await File.WriteAllTextAsync(Path.Combine(context.ArtifactsDir, "manifest.json"), json);
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}
