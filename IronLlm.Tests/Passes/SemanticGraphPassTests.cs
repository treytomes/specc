using IronLlm.Graph;
using IronLlm.Tests.Fixtures;

namespace IronLlm.Tests.Passes;

public class SemanticGraphPassTests
{
    private static IronLlm.Passes.CompilationContext BuildContext() => PipelineFixtures.AfterGraph();

    [Fact]
    public void SemanticGraph_IsNotNull()
    {
        Assert.NotNull(BuildContext().SemanticGraph);
    }

    [Fact]
    public void Graph_ContainsOneProgramNode_NamedFizzBuzz()
    {
        var ctx = BuildContext();
        var programs = ctx.SemanticGraph!.Nodes.OfType<ProgramNode>().ToList();
        Assert.Single(programs);
        Assert.Equal("FizzBuzz", programs[0].Name);
    }

    [Fact]
    public void Graph_ContainsOneLoopNode_From1To100()
    {
        var ctx = BuildContext();
        var loops = ctx.SemanticGraph!.Nodes.OfType<LoopNode>().ToList();
        Assert.Single(loops);
        Assert.Equal(1, loops[0].From);
        Assert.Equal(100, loops[0].To);
    }

    [Fact]
    public void Graph_ContainsFourBranchNodes()
    {
        var ctx = BuildContext();
        var branches = ctx.SemanticGraph!.Nodes.OfType<BranchNode>().ToList();
        Assert.Equal(4, branches.Count);
    }

    [Fact]
    public void Graph_BranchConditions_AreAllPresent()
    {
        var ctx = BuildContext();
        var conditions = ctx.SemanticGraph!.Nodes.OfType<BranchNode>()
            .Select(b => b.Condition).ToHashSet();
        Assert.Contains("divisible_by_15", conditions);
        Assert.Contains("divisible_by_3",  conditions);
        Assert.Contains("divisible_by_5",  conditions);
        Assert.Contains("default",         conditions);
    }

    [Fact]
    public void Graph_ContainsVariableNode_Named_n()
    {
        var ctx = BuildContext();
        var vars = ctx.SemanticGraph!.Nodes.OfType<VariableNode>().ToList();
        Assert.Single(vars);
        Assert.Equal("n", vars[0].Name);
        Assert.Equal("int", vars[0].Type);
    }

    [Fact]
    public void AllNodes_HaveNonEmptyIds()
    {
        var ctx = BuildContext();
        foreach (var node in ctx.SemanticGraph!.Nodes)
            Assert.NotEqual(Guid.Empty, node.Id);
    }

    [Fact]
    public void Graph_HasContainsEdge_FromProgram_ToLoop()
    {
        var ctx     = BuildContext();
        var graph   = ctx.SemanticGraph!;
        var program = graph.Nodes.OfType<ProgramNode>().Single();
        var loop    = graph.Nodes.OfType<LoopNode>().Single();
        var edge    = graph.Edges.FirstOrDefault(e =>
            e.From == program.Id && e.To == loop.Id && e.Type == EdgeType.Contains);
        Assert.NotNull(edge);
    }

    [Fact]
    public void Graph_HasContainsEdge_FromProgram_ToEachBranch()
    {
        var ctx     = BuildContext();
        var graph   = ctx.SemanticGraph!;
        var program = graph.Nodes.OfType<ProgramNode>().Single();
        var branches = graph.Nodes.OfType<BranchNode>().ToList();
        foreach (var branch in branches)
        {
            var edge = graph.Edges.FirstOrDefault(e =>
                e.From == program.Id && e.To == branch.Id && e.Type == EdgeType.Contains);
            Assert.NotNull(edge);
        }
    }

    [Fact]
    public void Graph_HasDependsOnEdges_ForDivisorBranches()
    {
        var ctx   = BuildContext();
        var graph = ctx.SemanticGraph!;
        var divisorBranches = new[] { "divisible_by_15", "divisible_by_3", "divisible_by_5" };
        foreach (var condition in divisorBranches)
        {
            var branch = graph.Nodes.OfType<BranchNode>().Single(b => b.Condition == condition);
            var edge   = graph.Edges.FirstOrDefault(e =>
                e.From == branch.Id && e.Type == EdgeType.DependsOn);
            Assert.NotNull(edge);
        }
    }

    [Fact]
    public void Graph_DefaultBranch_HasNoDependsOnEdge()
    {
        var ctx    = BuildContext();
        var graph  = ctx.SemanticGraph!;
        var branch = graph.Nodes.OfType<BranchNode>().Single(b => b.Condition == "default");
        var edge   = graph.Edges.FirstOrDefault(e =>
            e.From == branch.Id && e.Type == EdgeType.DependsOn);
        Assert.Null(edge);
    }

    [Fact]
    public async Task Execute_Throws_WhenRawSpecNotSet()
    {
        var ctx = PipelineFixtures.MakeContext();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            PipelineFixtures.MakeSemanticGraphPass().ExecuteAsync(ctx));
    }

    [Fact]
    public async Task Execute_EmitsWarning_WhenNoLoopSection()
    {
        var logger = new IronLlm.Tests.Fixtures.FakeLogger<IronLlm.Passes.SemanticGraphPass>();
        var pass   = new IronLlm.Passes.SemanticGraphPass(logger);
        var ctx    = PipelineFixtures.MakeContext();
        ctx.RawSpec = "program: NoLoop\nbranch:\n  condition: c\n  true_output: \"x\"\nvariable:\n  name: n\n  type: int";
        await pass.ExecuteAsync(ctx);
        Assert.Contains(logger.Records,
            r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                 r.Message.Contains("loop"));
    }

    [Fact]
    public async Task Execute_EmitsWarning_WhenNoVariableSection()
    {
        var logger = new IronLlm.Tests.Fixtures.FakeLogger<IronLlm.Passes.SemanticGraphPass>();
        var pass   = new IronLlm.Passes.SemanticGraphPass(logger);
        var ctx    = PipelineFixtures.MakeContext();
        ctx.RawSpec = "program: NoVar\nloop:\n  from: 1\n  to: 10\nbranch:\n  condition: c\n  true_output: \"x\"";
        await pass.ExecuteAsync(ctx);
        Assert.Contains(logger.Records,
            r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                 r.Message.Contains("variable"));
    }
}
