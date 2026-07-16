using IronLlm.Graph;
using IronLlm.Passes;
using IronLlm.Tests.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronLlm.Tests.Passes;

/// <summary>
/// Uses a deterministic one-hot mock embedder so tests are hermetic and fast.
///
/// The reference corpus has 8 entries (indices 0-7 map to the ReferenceCorpus array
/// order in SemanticNormalizationPass). The mock assigns each description a unit
/// vector in the dimension equal to its position in the corpus.
///
/// Corpus order (matches SemanticNormalizationPass.ReferenceCorpus):
///   0=Program  1=Loop  2=Branch  3=Print
///   4=Modulo   5=Variable  6=Constant  7=Comparison
/// </summary>
public class SemanticNormalizationPassTests
{
    private const int Dims = 8;

    // Builds a unit vector with a 1.0 in the given dimension, 0s elsewhere.
    private static float[] OneHot(int dim)
    {
        var v = new float[Dims];
        v[dim] = 1f;
        return v;
    }

    // Mock embedder: maps each description to its corpus-index one-hot vector.
    // Unknown descriptions get dimension 0 (Program) — tests control input carefully.
    private sealed class OneHotEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        // Ordered to match SemanticNormalizationPass.ReferenceCorpus descriptions exactly.
        private static readonly string[] CorpusDescriptions =
        [
            "A named executable program.",
            "A loop that iterates over a range of integers.",
            "A conditional if-then branch.",
            "Print a value or string to standard output.",
            "Integer modulo, the remainder after division.",
            "An integer variable declaration.",
            "An integer literal constant.",
            "A comparison between two integer values.",
        ];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = values.Select(v =>
            {
                var idx = Array.IndexOf(CorpusDescriptions, v);
                return new Embedding<float>(OneHot(idx >= 0 ? idx : 0));
            }).ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public EmbeddingGeneratorMetadata Metadata => new("OneHotEmbedder", null, null);
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static SemanticNormalizationPass MakePass() =>
        new(new OneHotEmbedder(), NullLogger<SemanticNormalizationPass>.Instance);

    // Build a context with a minimal graph + the corresponding one-hot embeddings.
    private static (SemanticNormalizationPass Pass, CompilationContext Context) BuildContext(
        IEnumerable<(Node Node, int CorpusDim)> entries)
    {
        var graph = new SemanticGraph();
        var embeddings = new List<NodeEmbedding>();

        foreach (var (node, dim) in entries)
        {
            graph.Add(node);
            embeddings.Add(new NodeEmbedding(node.Id, node.Label, OneHot(dim)));
        }

        var ctx = PipelineFixtures.MakeContext();
        ctx.SemanticGraph = graph;
        ctx.Embeddings    = embeddings;

        return (MakePass(), ctx);
    }

    [Fact]
    public async Task Execute_SetsGraphNormalized_True()
    {
        var node = new PrintNode(Guid.NewGuid(), "Print:Fizz", "Fizz");
        var (pass, ctx) = BuildContext([(node, 3)]);  // 3=Print
        await pass.ExecuteAsync(ctx);
        Assert.True(ctx.GraphNormalized);
    }

    [Fact]
    public async Task Execute_NormalizesLabel_ForPrintNode()
    {
        var node = new PrintNode(Guid.NewGuid(), "write Fizz to output", "Fizz");
        var (pass, ctx) = BuildContext([(node, 3)]);  // 3=Print
        await pass.ExecuteAsync(ctx);
        Assert.Equal("Print:Fizz", ctx.SemanticGraph!.Nodes[0].Label);
    }

    [Fact]
    public async Task Execute_NormalizesLabel_ForLoopNode()
    {
        var node = new LoopNode(Guid.NewGuid(), "iterate from 1 to 100", 1, 100);
        var (pass, ctx) = BuildContext([(node, 1)]);  // 1=Loop
        await pass.ExecuteAsync(ctx);
        Assert.Equal("Loop:1..100", ctx.SemanticGraph!.Nodes[0].Label);
    }

    [Fact]
    public async Task Execute_NormalizesLabel_ForVariableNode()
    {
        var node = new VariableNode(Guid.NewGuid(), "counter variable n", "n", "int");
        var (pass, ctx) = BuildContext([(node, 5)]);  // 5=Variable
        await pass.ExecuteAsync(ctx);
        Assert.Equal("Var:n", ctx.SemanticGraph!.Nodes[0].Label);
    }

    [Fact]
    public async Task Execute_ReclassifiesNode_WhenKindMismatch()
    {
        // A PrintNode whose embedding points to Print (dim 3) — but label is wrong type.
        // Use a BranchNode record but give it a Print embedding so it gets reclassified.
        var node = new BranchNode(Guid.NewGuid(), "output the string Buzz", "output_the_string_Buzz");
        var (pass, ctx) = BuildContext([(node, 3)]);  // 3=Print — mismatch with BranchNode
        await pass.ExecuteAsync(ctx);
        Assert.IsType<PrintNode>(ctx.SemanticGraph!.Nodes[0]);
    }

    [Fact]
    public async Task Execute_EmitsWarning_WhenNodeReclassified()
    {
        var logger = new FakeLogger<SemanticNormalizationPass>();
        var node   = new BranchNode(Guid.NewGuid(), "output the string Buzz", "output_the_string_Buzz");
        var graph  = new SemanticGraph();
        graph.Add(node);
        var ctx = PipelineFixtures.MakeContext();
        ctx.SemanticGraph = graph;
        ctx.Embeddings    = [new NodeEmbedding(node.Id, node.Label, OneHot(3))];

        var pass = new SemanticNormalizationPass(new OneHotEmbedder(), logger);
        await pass.ExecuteAsync(ctx);

        Assert.Contains(logger.Records,
            r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                 r.Message.Contains("reclassified"));
    }

    [Fact]
    public async Task Execute_Throws_WhenSimilarityBelowThreshold()
    {
        var node = new PrintNode(Guid.NewGuid(), "mystery node", "???");
        var ctx  = PipelineFixtures.MakeContext();
        ctx.SemanticGraph = new SemanticGraph();
        ctx.SemanticGraph.Add(node);

        // Assign a zero vector — cosine similarity will be 0, below threshold.
        ctx.Embeddings = [new NodeEmbedding(node.Id, node.Label, new float[Dims])];

        await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
    }

    [Fact]
    public async Task Execute_Throws_WhenSemanticGraphNotSet()
    {
        var ctx = PipelineFixtures.MakeContext();
        await Assert.ThrowsAnyAsync<Exception>(() => MakePass().ExecuteAsync(ctx));
    }

    [Fact]
    public async Task Execute_Throws_WhenEmbeddingsNotSet()
    {
        var ctx = PipelineFixtures.MakeContext();
        ctx.SemanticGraph = new SemanticGraph();
        ctx.SemanticGraph.Add(new PrintNode(Guid.NewGuid(), "Print:Fizz", "Fizz"));
        await Assert.ThrowsAnyAsync<Exception>(() => MakePass().ExecuteAsync(ctx));
    }

    [Fact]
    public async Task LoadFromArtifact_RestoresGraphAndFlag()
    {
        // Round-trip: run the pass, serialize the result, then load from artifact.
        var node = new PrintNode(Guid.NewGuid(), "Print:Fizz", "Fizz");
        var (pass, ctx) = BuildContext([(node, 3)]);
        await pass.ExecuteAsync(ctx);

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = ctx.SemanticGraph!.Nodes,
            edges = ctx.SemanticGraph.Edges,
        });
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, json);
            var loaded = PipelineFixtures.MakeContext();
            await MakePass().LoadFromArtifactAsync(tmp, loaded);
            Assert.True(loaded.GraphNormalized);
            Assert.NotNull(loaded.SemanticGraph);
            Assert.Single(loaded.SemanticGraph.Nodes);
        }
        finally { File.Delete(tmp); }
    }

    // ── CosineSimilarity unit tests ───────────────────────────────────────────

    [Fact]
    public void CosineSimilarity_IsOne_ForIdenticalVectors()
    {
        var v = new float[] { 0.5f, 0.5f, 0.5f };
        Assert.Equal(1f, SemanticNormalizationPass.CosineSimilarity(v, v), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_IsZero_ForOrthogonalVectors()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };
        Assert.Equal(0f, SemanticNormalizationPass.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_IsZero_ForZeroVector()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 0f };
        Assert.Equal(0f, SemanticNormalizationPass.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void BestMatch_ReturnsCorrectKind_ForOneHotVector()
    {
        var refs = new (string Kind, float[] Vector)[]
        {
            ("Program",  OneHot(0)),
            ("Loop",     OneHot(1)),
            ("Print",    OneHot(3)),
        };
        var (kind, score) = SemanticNormalizationPass.BestMatch(OneHot(3), refs);
        Assert.Equal("Print", kind);
        Assert.Equal(1f, score, precision: 5);
    }
}
