using System.Text.Json;
using Specc.Graph;

namespace Specc.Passes.Repository;

/// <summary>Persists and retrieves compilation units from the on-disk graph repository.</summary>
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

    /// <summary>Stores the completed compilation unit in the repository, skipping if the spec hash already exists or acceptance failed.</summary>
    public static async Task PersistAsync(CompilationContext context, DateTimeOffset? compiledAt = null)
    {
        if (context.SpecPath is null) return;

        // Never persist a compilation whose acceptance verification explicitly failed.
        // AcceptancePassed == null means verification didn't run (loaded from artifact) — allow persist.
        // AcceptancePassed == false means assertions ran and failed — block persist.
        if (context.AcceptancePassed == false) return;

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

        var specText = File.Exists(context.SpecPath)
            ? await File.ReadAllTextAsync(context.SpecPath)
            : "";

        var unit = new CompiledUnit(
            Id:                Guid.NewGuid(),
            SpecHash:          specHash,
            ProgramName:       programName,
            CompiledAt:        (compiledAt ?? DateTimeOffset.UtcNow).ToString("O"),
            SemanticGraphPath: semanticGraphPath,
            EmbeddingsPath:    embeddingsPath,
            CfgPath:           cfgPath,
            StackIrPath:       stackIrPath,
            MsilPath:          msilPath,
            SpecText:          specText,
            AcceptancePassed:  context.AcceptancePassed ?? false,
            AssertionCount:    context.AssertionCount
        );

        index.Units.Add(unit);

        var json = JsonSerializer.Serialize(index, JsonOpts);
        await File.WriteAllTextAsync(indexPath, json);
    }

    /// <summary>Returns up to <paramref name="topK"/> prior specs whose stored spec text best matches the given classifier tags.</summary>
    public static async Task<List<(string ProgramName, string SpecText)>> FindPriorsByTagsAsync(
        string repositoryPath, string[] tags, int topK = 2)
    {
        if (!Directory.Exists(repositoryPath)) return [];
        var indexPath = Path.Combine(repositoryPath, "index.json");
        if (!File.Exists(indexPath)) return [];

        var index = await LoadIndexAsync(indexPath);

        static int CountMatches(string specText, string[] tags) =>
            tags.Count(tag => specText.Contains(tag + ":", StringComparison.Ordinal));

        return index.Units
            .Where(u => !string.IsNullOrEmpty(u.SpecText))
            .Select(u => (u.ProgramName, u.SpecText, Score: CountMatches(u.SpecText, tags),
                          Verified: u.AcceptancePassed && u.AssertionCount > 0))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Verified)   // verified entries rank above unverified at same score
            .ThenByDescending(x => x.SpecText.Length)
            .Take(topK)
            .Select(x => (x.ProgramName, x.SpecText))
            .ToList();
    }

    /// <summary>Returns up to <paramref name="topK"/> nodes from prior compilations whose embeddings are cosine-similar to nodes in the current context.</summary>
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
