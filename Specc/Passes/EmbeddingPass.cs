using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Specc.Graph;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

// Embeds each graph node independently using mxbai-embed-large.
// Embeddings are metadata — they don't change the graph structure.
/// <summary>Produces a floating-point embedding vector for every non-assertion graph node using mxbai-embed-large.</summary>
[ExcludeFromCodeCoverage(Justification = "Requires live Ollama; covered by scripts/test.sh")]
public class EmbeddingPass : ICompilerPass
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ILogger<EmbeddingPass> _logger;

    /// <summary>Initialises the pass with an embedding generator and a logger.</summary>
    public EmbeddingPass(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ILogger<EmbeddingPass> logger)
    {
        _embedder = embedder;
        _logger   = logger;
    }

    /// <inheritdoc/>
    public string Name          => "03-Embeddings";
    /// <inheritdoc/>
    public string? ArtifactFile  => "03-embeddings.json";

    /// <inheritdoc/>
    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json  = await File.ReadAllTextAsync(artifactPath);
        var opts  = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var dtos  = JsonSerializer.Deserialize<List<NodeEmbeddingDto>>(json, opts) ?? [];
        context.Embeddings = dtos
            .Select(d => new NodeEmbedding(d.NodeId, d.Label, d.Vector))
            .ToList();
    }

    private sealed class NodeEmbeddingDto
    {
        public Guid    NodeId { get; set; }
        public string  Label  { get; set; } = "";
        public float[] Vector { get; set; } = [];
    }

    /// <inheritdoc/>
    public Task ExecuteAsync(CompilationContext context)
    {
        var graph = context.SemanticGraph ?? throw new InvalidOperationException("SemanticGraph not set");
        var embedCount = graph.Nodes.Count(n => n is not AssertionNode);
        _logger.LogInformation("Embedding {Count} nodes", embedCount);
        return EmbedAllAsync(graph, context);
    }

    private async Task EmbedAllAsync(SemanticGraph graph, CompilationContext context)
    {
        var sw      = Stopwatch.StartNew();
        var tasks   = graph.Nodes.Where(n => n is not AssertionNode).Select(n => EmbedNodeAsync(n));
        var results = await Task.WhenAll(tasks);
        context.Embeddings = [.. results];
        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
    }

    private async Task<NodeEmbedding> EmbedNodeAsync(Node node)
    {
        var description = Describe(node);
        var result = await _embedder.GenerateAsync([description]);
        var vector = result[0].Vector.ToArray();
        _logger.LogDebug("Embedded {Label} — {Dims}d", node.Label, vector.Length);
        return new NodeEmbedding(node.Id, node.Label, vector);
    }

    private static string Describe(Node node) => node switch
    {
        ProgramNode p    => $"A program named {p.Name}.",
        LoopNode l       => $"Iterates integers sequentially from {l.From} to {l.To}.",
        BranchNode b     => $"A conditional branch: {b.Condition.Replace('_', ' ')}.",
        ModuloNode m     => $"Computes the remainder after division by {m.Divisor}.",
        ComparisonNode c => c.Value != 0
            ? $"Compares a value to {c.Value} using the {c.Op} operator."
            : $"Compares two values using the {c.Op} operator.",
        PrintNode pr     => $"Outputs the value \"{pr.Template}\" to the console.",
        VariableNode v   => $"A variable named {v.Name} of type {v.Type}.",
        ConstantNode c   => $"The integer constant {c.Value}.",
        _                => node.Label,
    };
}
