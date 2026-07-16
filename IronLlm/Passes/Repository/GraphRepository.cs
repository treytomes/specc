using System.Text.Json;
using IronLlm.Graph;

namespace IronLlm.Passes.Repository;

public static class GraphRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] ArtifactsToCopy =
    [
        "02-semantic-graph.json",
        "03-embeddings.json",
        "04-cfg.json",
        "05-stackir.json",
        "06-program.il",
    ];

    public static async Task PersistAsync(CompilationContext context, DateTimeOffset? compiledAt = null)
    {
        if (context.SpecPath is null) return;

        var specHash = File.Exists(context.SpecPath)
            ? ManifestWriter.HashFile(context.SpecPath)
            : "unknown";

        var unitDir = Path.Combine(context.RepositoryPath, specHash[..8]);
        Directory.CreateDirectory(unitDir);

        // Load or initialise the index.
        var indexPath = Path.Combine(context.RepositoryPath, "index.json");
        var index = await LoadIndexAsync(indexPath);

        // Skip if this spec hash already has an entry.
        if (index.Units.Any(u => u.SpecHash == specHash))
            return;

        // Copy artifacts that exist.
        string Copy(string name)
        {
            var src = Path.Combine(context.ArtifactsDir, name);
            var dst = Path.Combine(unitDir, name);
            if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
            return dst;
        }

        var semanticGraphPath = Copy("02-semantic-graph.json");
        var embeddingsPath    = Copy("03-embeddings.json");
        var cfgPath           = Copy("04-cfg.json");
        var stackIrPath       = Copy("05-stackir.json");
        var msilPath          = Copy("06-program.il");

        var programName = context.SemanticGraph?.Nodes
            .OfType<ProgramNode>()
            .Select(n => n.Name)
            .FirstOrDefault() ?? "Unknown";

        var unit = new CompiledUnit(
            Id:                Guid.NewGuid(),
            SpecHash:          specHash,
            ProgramName:       programName,
            CompiledAt:        (compiledAt ?? DateTimeOffset.UtcNow).ToString("O"),
            SemanticGraphPath: semanticGraphPath,
            EmbeddingsPath:    embeddingsPath,
            CfgPath:           cfgPath,
            StackIrPath:       stackIrPath,
            MsilPath:          msilPath
        );

        index.Units.Add(unit);

        var json = JsonSerializer.Serialize(index, JsonOpts);
        await File.WriteAllTextAsync(indexPath, json);
    }

    public static async Task<List<SimilarPrior>> FindSimilarAsync(
        CompilationContext context, float threshold = 0.85f, int topK = 5)
    {
        if (!Directory.Exists(context.RepositoryPath))
            return [];

        var indexPath = Path.Combine(context.RepositoryPath, "index.json");
        if (!File.Exists(indexPath))
            return [];

        var index = await LoadIndexAsync(indexPath);
        if (index.Units.Count == 0)
            return [];

        if (context.Embeddings.Count == 0)
            return [];

        var results = new List<SimilarPrior>();

        foreach (var unit in index.Units)
        {
            if (!File.Exists(unit.EmbeddingsPath))
                continue;

            var storedEmbeddings = await LoadEmbeddingsAsync(unit.EmbeddingsPath);

            foreach (var stored in storedEmbeddings)
            {
                foreach (var current in context.Embeddings)
                {
                    var similarity = SemanticNormalizationPass.CosineSimilarity(stored.Vector, current.Vector);
                    if (similarity >= threshold)
                    {
                        results.Add(new SimilarPrior(
                            UnitId:      unit.Id,
                            ProgramName: unit.ProgramName,
                            NodeId:      stored.NodeId,
                            NodeLabel:   stored.Label,
                            Similarity:  similarity
                        ));
                    }
                }
            }
        }

        return results
            .OrderByDescending(r => r.Similarity)
            .Take(topK)
            .ToList();
    }

    private static async Task<RepositoryIndex> LoadIndexAsync(string indexPath)
    {
        if (!File.Exists(indexPath))
            return new RepositoryIndex([]);

        var json = await File.ReadAllTextAsync(indexPath);
        return JsonSerializer.Deserialize<RepositoryIndex>(json, JsonOpts)
               ?? new RepositoryIndex([]);
    }

    private static async Task<List<NodeEmbeddingDto>> LoadEmbeddingsAsync(string embeddingsPath)
    {
        var json = await File.ReadAllTextAsync(embeddingsPath);
        return JsonSerializer.Deserialize<List<NodeEmbeddingDto>>(json, JsonOpts) ?? [];
    }

    private sealed class NodeEmbeddingDto
    {
        public Guid    NodeId { get; set; }
        public string  Label  { get; set; } = "";
        public float[] Vector { get; set; } = [];
    }
}
