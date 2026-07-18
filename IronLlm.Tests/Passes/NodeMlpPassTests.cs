using IronLlm.Graph;
using IronLlm.Learning;
using IronLlm.Passes;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronLlm.Tests.Passes;

public class NodeMlpTests
{
    [Fact]
    public void Forward_ProducesOutputDimVector()
    {
        var mlp = NodeMlp.CreateRandom(new Random(1));
        var nodeEmb     = new float[1024];
        var neighborMean = new float[1024];
        var output = mlp.Forward(nodeEmb, neighborMean);
        Assert.Equal(NodeMlp.OutputDim, output.Length);
    }

    [Fact]
    public void Forward_ZeroInput_ProducesDeterministicOutput()
    {
        var mlp = NodeMlp.CreateRandom(new Random(7));
        var zero = new float[1024];
        var a = mlp.Forward(zero, zero);
        var b = mlp.Forward(zero, zero);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Forward_NonZeroInput_DiffersFromZeroInput()
    {
        var mlp = NodeMlp.CreateRandom(new Random(3));
        var zero    = new float[1024];
        var nonzero = Enumerable.Range(0, 1024).Select(i => (float)i / 1024f).ToArray();
        var outZero    = mlp.Forward(zero, zero);
        var outNonzero = mlp.Forward(nonzero, zero);
        Assert.False(outZero.SequenceEqual(outNonzero));
    }

    [Fact]
    public void CreateRandom_SameSeed_ProducesIdenticalWeights()
    {
        var a = NodeMlp.CreateRandom(new Random(42));
        var b = NodeMlp.CreateRandom(new Random(42));
        Assert.Equal(a.W1[0][0], b.W1[0][0]);
        Assert.Equal(a.W2[0][0], b.W2[0][0]);
    }
}

public class NodeMlpPassTests
{
    private static CompilationContext MakeContext(SemanticGraph graph, List<NodeEmbedding> embeddings)
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "test.spec",
            ArtifactsDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            RepositoryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
        };
        ctx.SemanticGraph = graph;
        ctx.Embeddings    = embeddings;
        return ctx;
    }

    [Fact]
    public async Task Execute_ReplacesEmbeddingsWithRefinedVectors()
    {
        var graph = new SemanticGraph();
        var prog = new ProgramNode(Guid.NewGuid(), "Program:Test", "Test");
        graph.Add(prog);

        var rawVec = Enumerable.Range(0, 1024).Select(i => (float)i / 1024f).ToArray();
        var embeddings = new List<NodeEmbedding>
        {
            new NodeEmbedding(prog.Id, "Program:Test", rawVec),
        };

        var ctx  = MakeContext(graph, embeddings);
        var pass = new NodeMlpPass(NullLogger<NodeMlpPass>.Instance);
        await pass.ExecuteAsync(ctx);

        Assert.Single(ctx.Embeddings);
        Assert.Equal(1024, ctx.Embeddings[0].Vector.Length);
        // Refined vector is different from raw (MLP with random weights transforms it)
        Assert.False(rawVec.SequenceEqual(ctx.Embeddings[0].Vector));
    }

    [Fact]
    public async Task Execute_NodeWithNoEmbedding_Skipped()
    {
        var graph = new SemanticGraph();
        var prog  = new ProgramNode(Guid.NewGuid(), "Program:Test", "Test");
        var loop  = new LoopNode(Guid.NewGuid(), "Loop:1..10", 1, 10);
        graph.Add(prog);
        graph.Add(loop);
        graph.Connect(prog.Id, loop.Id, EdgeType.Contains);

        // Only embed prog, not loop
        var rawVec = new float[1024];
        var embeddings = new List<NodeEmbedding>
        {
            new NodeEmbedding(prog.Id, "Program:Test", rawVec),
        };

        var ctx  = MakeContext(graph, embeddings);
        var pass = new NodeMlpPass(NullLogger<NodeMlpPass>.Instance);
        await pass.ExecuteAsync(ctx);

        // Only prog was embedded so only prog appears in refined output
        Assert.Single(ctx.Embeddings);
        Assert.Equal(prog.Id, ctx.Embeddings[0].NodeId);
    }

    [Fact]
    public async Task Execute_WritesWeightsFile()
    {
        var graph = new SemanticGraph();
        var prog = new ProgramNode(Guid.NewGuid(), "Program:Test", "Test");
        graph.Add(prog);

        var embeddings = new List<NodeEmbedding>
        {
            new NodeEmbedding(prog.Id, "Program:Test", new float[1024]),
        };

        var ctx  = MakeContext(graph, embeddings);
        var pass = new NodeMlpPass(NullLogger<NodeMlpPass>.Instance);
        await pass.ExecuteAsync(ctx);

        var weightsPath = Path.Combine(ctx.RepositoryPath, "node-mlp-weights.json");
        Assert.True(File.Exists(weightsPath));
    }

    [Fact]
    public async Task Execute_LoadsExistingWeights_SecondRun()
    {
        var graph = new SemanticGraph();
        var prog = new ProgramNode(Guid.NewGuid(), "Program:Test", "Test");
        graph.Add(prog);
        var rawVec = new float[1024];
        var embeddings1 = new List<NodeEmbedding> { new(prog.Id, "Program:Test", rawVec) };

        var ctx1 = MakeContext(graph, embeddings1);
        var pass = new NodeMlpPass(NullLogger<NodeMlpPass>.Instance);
        await pass.ExecuteAsync(ctx1);

        // Second run using the same repo path — should load saved weights and produce same output
        var embeddings2 = new List<NodeEmbedding> { new(prog.Id, "Program:Test", rawVec) };
        var ctx2 = new CompilationContext
        {
            SpecPath       = "test.spec",
            ArtifactsDir   = ctx1.ArtifactsDir,
            RepositoryPath = ctx1.RepositoryPath,
            SemanticGraph  = graph,
            Embeddings     = embeddings2,
        };
        await pass.ExecuteAsync(ctx2);

        Assert.Equal(ctx1.Embeddings[0].Vector, ctx2.Embeddings[0].Vector);
    }

    [Fact]
    public async Task Execute_NeighborEmbeddingInfluencesOutput()
    {
        var graph = new SemanticGraph();
        var prog = new ProgramNode(Guid.NewGuid(), "Program:Test", "Test");
        var loop = new LoopNode(Guid.NewGuid(), "Loop:1..10", 1, 10);
        graph.Add(prog);
        graph.Add(loop);
        graph.Connect(prog.Id, loop.Id, EdgeType.Contains);

        var progVec = new float[1024];
        var loopVec = Enumerable.Range(0, 1024).Select(i => 1f).ToArray();

        // Run with loop embedding present
        var embeddings = new List<NodeEmbedding>
        {
            new(prog.Id, "Program:Test", progVec),
            new(loop.Id, "Loop:1..10",   loopVec),
        };
        var ctx1 = MakeContext(graph, embeddings);
        await new NodeMlpPass(NullLogger<NodeMlpPass>.Instance).ExecuteAsync(ctx1);

        // Run with no loop embedding (neighbor mean is zero)
        var embeddings2 = new List<NodeEmbedding>
        {
            new(prog.Id, "Program:Test", progVec),
        };
        var ctx2 = MakeContext(graph, embeddings2);
        await new NodeMlpPass(NullLogger<NodeMlpPass>.Instance).ExecuteAsync(ctx2);

        var progOut1 = ctx1.Embeddings.First(e => e.NodeId == prog.Id).Vector;
        var progOut2 = ctx2.Embeddings.First(e => e.NodeId == prog.Id).Vector;

        // When the loop neighbor has a non-zero embedding the program node's output differs
        Assert.False(progOut1.SequenceEqual(progOut2),
            "Neighbor embedding should influence the program node's refined output");
    }
}
