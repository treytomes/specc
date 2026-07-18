using System.Text.Json;
using IronLlm.Graph;
using IronLlm.Learning;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

// Runs each node's raw embedding through the per-kind MLP, replacing raw vectors
// with refined vectors in context.Embeddings. Operates in-place so downstream passes
// (SemanticNormalizationPass, RepositoryRetrievalPass) see the refined vectors automatically.
//
// Neighbourhood aggregation uses only Contains and DependsOn edges pointing *away* from the
// node (the Contains DAG). CFG successor edges are excluded — they can form cycles and
// would make gradient computation recurrent during Spec 41 training.
public class NodeMlpPass : ICompilerPass
{
    private readonly ILogger<NodeMlpPass> _logger;

    public NodeMlpPass(ILogger<NodeMlpPass> logger) => _logger = logger;

    public string  Name         => "03a-NodeMlp";
    public string? ArtifactFile => "03a-refined-embeddings.json";

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json  = await File.ReadAllTextAsync(artifactPath);
        var opts  = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var dtos  = JsonSerializer.Deserialize<List<NodeEmbeddingDto>>(json, opts) ?? [];
        context.Embeddings = dtos
            .Select(d => new NodeEmbedding(d.NodeId, d.NodeLabel, d.Vector))
            .ToList();
    }

    public Task ExecuteAsync(CompilationContext context)
    {
        var graph = context.SemanticGraph
            ?? throw new InvalidOperationException("SemanticGraph not set");

        if (context.Embeddings.Count == 0)
            throw new InvalidOperationException("Embeddings not set — run EmbeddingPass first");

        var registry = NodeMlpRegistry.LoadOrCreate(context.RepositoryPath);
        var embMap   = context.Embeddings.ToDictionary(e => e.NodeId);

        var refined = new List<NodeEmbedding>(context.Embeddings.Count);
        var zero    = new float[NodeMlp.OutputDim];

        foreach (var node in graph.Nodes)
        {
            if (!embMap.TryGetValue(node.Id, out var emb))
            {
                _logger.LogDebug("No embedding for node '{Label}' — skipping MLP", node.Label);
                continue;
            }

            var neighborMean = ComputeNeighborMean(node.Id, graph, embMap, zero);
            var refinedVec   = registry.Refine(node, emb.Vector, neighborMean);
            refined.Add(new NodeEmbedding(node.Id, emb.NodeLabel, refinedVec));
        }

        context.Embeddings = refined;

        // Persist weights (no-op if they already exist on disk; writes on first run).
        registry.Save(context.RepositoryPath);

        _logger.LogInformation("Pass {Name} completed — {Count} nodes refined", Name, refined.Count);
        return Task.CompletedTask;
    }

    private static float[] ComputeNeighborMean(
        Guid nodeId,
        SemanticGraph graph,
        Dictionary<Guid, NodeEmbedding> embMap,
        float[] zero)
    {
        var neighborIds = graph.Edges
            .Where(e => e.From == nodeId &&
                        (e.Type == EdgeType.Contains || e.Type == EdgeType.DependsOn))
            .Select(e => e.To)
            .ToList();

        if (neighborIds.Count == 0) return zero;

        var vecs = neighborIds
            .Where(embMap.ContainsKey)
            .Select(id => embMap[id].Vector)
            .ToList();

        if (vecs.Count == 0) return zero;

        var mean = new float[vecs[0].Length];
        foreach (var v in vecs)
            for (var i = 0; i < mean.Length; i++)
                mean[i] += v[i];
        for (var i = 0; i < mean.Length; i++)
            mean[i] /= vecs.Count;
        return mean;
    }

    private sealed class NodeEmbeddingDto
    {
        public Guid    NodeId    { get; set; }
        public string  NodeLabel { get; set; } = "";
        public float[] Vector    { get; set; } = [];
    }
}
