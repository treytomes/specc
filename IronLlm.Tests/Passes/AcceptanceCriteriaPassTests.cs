using IronLlm.Graph;
using IronLlm.Passes;
using IronLlm.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace IronLlm.Tests.Passes;

public class AcceptanceCriteriaPassTests
{
    private static AcceptanceCriteriaPass MakePass() =>
        new(NullLogger<AcceptanceCriteriaPass>.Instance);

    private static async Task<CompilationContext> BuildContextAsync()
    {
        var ctx = PipelineFixtures.AfterGraph();
        await MakePass().ExecuteAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task Execute_Produces_100_Assertions_ForFizzBuzz()
    {
        var ctx = await BuildContextAsync();
        Assert.Equal(100, ctx.Assertions.Count);
    }

    [Fact]
    public async Task Execute_Assertions_AreOrderedByIteration()
    {
        var ctx = await BuildContextAsync();
        for (var i = 0; i < ctx.Assertions.Count; i++)
            Assert.Equal(i + 1, ctx.Assertions[i].Iteration);
    }

    [Fact]
    public async Task Execute_Iteration1_Expects_1()
    {
        Assert.Equal("1", (await BuildContextAsync()).Assertions[0].Expected);
    }

    [Fact]
    public async Task Execute_Iteration3_Expects_Fizz()
    {
        Assert.Equal("Fizz", (await BuildContextAsync()).Assertions[2].Expected);
    }

    [Fact]
    public async Task Execute_Iteration5_Expects_Buzz()
    {
        Assert.Equal("Buzz", (await BuildContextAsync()).Assertions[4].Expected);
    }

    [Fact]
    public async Task Execute_Iteration15_Expects_FizzBuzz()
    {
        Assert.Equal("FizzBuzz", (await BuildContextAsync()).Assertions[14].Expected);
    }

    [Fact]
    public async Task Execute_Iteration100_Expects_Buzz()
    {
        Assert.Equal("Buzz", (await BuildContextAsync()).Assertions[99].Expected);
    }

    [Fact]
    public async Task Execute_AddsAssertionNodesToGraph()
    {
        var ctx         = PipelineFixtures.AfterGraph();
        var nodesBefore = ctx.SemanticGraph!.Nodes.Count;
        await MakePass().ExecuteAsync(ctx);
        Assert.Equal(100, ctx.SemanticGraph.Nodes.OfType<AssertionNode>().Count());
        Assert.True(ctx.SemanticGraph.Nodes.Count > nodesBefore);
    }

    [Fact]
    public async Task Execute_AssertionNodes_ConnectedToProgramWithAssertsEdge()
    {
        var ctx            = await BuildContextAsync();
        var program        = ctx.SemanticGraph!.Nodes.OfType<ProgramNode>().Single();
        var assertionNodes = ctx.SemanticGraph.Nodes.OfType<AssertionNode>().ToList();
        foreach (var node in assertionNodes)
        {
            var edge = ctx.SemanticGraph.Edges.FirstOrDefault(e =>
                e.From == program.Id && e.To == node.Id && e.Type == EdgeType.Asserts);
            Assert.NotNull(edge);
        }
    }

    [Fact]
    public async Task Execute_AllMultiplesOf3_ExpectFizz_NotFizzBuzz()
    {
        var ctx   = await BuildContextAsync();
        var fizz3 = ctx.Assertions.Where(a => a.Iteration % 3 == 0 && a.Iteration % 5 != 0).ToList();
        Assert.NotEmpty(fizz3);
        Assert.All(fizz3, a => Assert.Equal("Fizz", a.Expected));
    }

    [Fact]
    public async Task Execute_AllMultiplesOf5_ExpectBuzz_NotFizzBuzz()
    {
        var ctx   = await BuildContextAsync();
        var buzz5 = ctx.Assertions.Where(a => a.Iteration % 5 == 0 && a.Iteration % 3 != 0).ToList();
        Assert.NotEmpty(buzz5);
        Assert.All(buzz5, a => Assert.Equal("Buzz", a.Expected));
    }

    [Fact]
    public async Task Execute_AllMultiplesOf15_ExpectFizzBuzz()
    {
        var ctx = await BuildContextAsync();
        var fb  = ctx.Assertions.Where(a => a.Iteration % 15 == 0).ToList();
        Assert.NotEmpty(fb);
        Assert.All(fb, a => Assert.Equal("FizzBuzz", a.Expected));
    }

    [Fact]
    public async Task Execute_NonMultiples_ExpectIntegerString()
    {
        var ctx   = await BuildContextAsync();
        var plain = ctx.Assertions.Where(a => a.Iteration % 3 != 0 && a.Iteration % 5 != 0).ToList();
        Assert.NotEmpty(plain);
        Assert.All(plain, a => Assert.Equal(a.Iteration.ToString(), a.Expected));
    }

    [Fact]
    public async Task Execute_Throws_WhenNoLoopNode()
    {
        var ctx = PipelineFixtures.MakeContext();
        ctx.SemanticGraph = new SemanticGraph();
        await Assert.ThrowsAnyAsync<Exception>(() => MakePass().ExecuteAsync(ctx));
    }

    [Fact]
    public async Task LoadFromArtifact_RestoresAssertions()
    {
        var original = new List<AssertionRecord>
        {
            new(1,  "1"),
            new(3,  "Fizz"),
            new(15, "FizzBuzz"),
        };
        var json = JsonSerializer.Serialize(original);
        var tmp  = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, json);
            var ctx = PipelineFixtures.MakeContext();
            await MakePass().LoadFromArtifactAsync(tmp, ctx);
            Assert.Equal(3,          ctx.Assertions.Count);
            Assert.Equal("Fizz",     ctx.Assertions[1].Expected);
            Assert.Equal("FizzBuzz", ctx.Assertions[2].Expected);
        }
        finally { File.Delete(tmp); }
    }
}
