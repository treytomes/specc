using Specc.Graph;
using Specc.Passes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Specc.Tests.Fixtures;

/// <summary>
/// Shared spec text and factory methods that build a pre-populated
/// CompilationContext at each pipeline stage, so tests start at the
/// right level without repeating setup boilerplate.
/// </summary>
internal static class PipelineFixtures
{
    public const string FizzBuzzSpecText = """
        program: FizzBuzz

        loop:
          from: 1
          to: 100

        branch:
          condition: divisible_by_15
          divisor: 15
          true_output: "FizzBuzz"

        branch:
          condition: divisible_by_3
          divisor: 3
          true_output: "Fizz"

        branch:
          condition: divisible_by_5
          divisor: 5
          true_output: "Buzz"

        branch:
          condition: default
          true_output: "{n}"

        variable:
          name: n
          type: int
        """;

    public static CompilationContext MakeContext(string? specPath = null) => new()
    {
        SpecPath     = specPath ?? "fake.spec",
        ArtifactsDir = Path.GetTempPath(),
    };

    public static ParseSpecPass MakeParseSpecPass()
        => new(NullLogger<ParseSpecPass>.Instance);

    public static SemanticGraphPass MakeSemanticGraphPass()
        => new(NullLogger<SemanticGraphPass>.Instance);

    public static CfgPass MakeCfgPass()
        => new(NullLogger<CfgPass>.Instance);

    public static StackIrPass MakeStackIrPass()
        => new(NullLogger<StackIrPass>.Instance);

    public static MsilGenerationPass MakeMsilGenerationPass()
        => new(NullLogger<MsilGenerationPass>.Instance);

    public static CompilationContext AfterParse()
    {
        var ctx = MakeContext();
        ctx.RawSpec = FizzBuzzSpecText;
        return ctx;
    }

    public static CompilationContext AfterGraph()
    {
        var ctx = AfterParse();
        MakeSemanticGraphPass().ExecuteAsync(ctx).GetAwaiter().GetResult();
        return ctx;
    }

    public static CompilationContext AfterCfg()
    {
        var ctx = AfterGraph();
        MakeCfgPass().ExecuteAsync(ctx).GetAwaiter().GetResult();
        return ctx;
    }

    public static CompilationContext AfterStackIr()
    {
        var ctx = AfterCfg();
        MakeStackIrPass().ExecuteAsync(ctx).GetAwaiter().GetResult();
        return ctx;
    }

    /// <summary>
    /// Builds the BubbleSort semantic graph directly (no LLM required).
    /// </summary>
    public static Specc.Graph.SemanticGraph BuildBubbleSortGraph()
    {
        var graph   = new Specc.Graph.SemanticGraph();
        var program = new Specc.Graph.ProgramNode(
            Guid.NewGuid(), "Program:BubbleSort", "BubbleSort");
        var arr = new Specc.Graph.ArrayNode(
            Guid.NewGuid(), "Array:arr[10]", "arr", "int", 10,
            new[] { 64, 34, 25, 12, 22, 11, 90, 45, 78, 3 });
        var outerLoop = new Specc.Graph.LoopNode(
            Guid.NewGuid(), "Loop:i:0..8", 0, 8);
        var innerLoop = new Specc.Graph.NestedLoopNode(
            Guid.NewGuid(), "NestedLoop:j<(8-i)", "j", 0, "(8-i)");
        var swapNode = new Specc.Graph.SwapNode(
            Guid.NewGuid(), "Swap:arr[j]<->arr[j+1]", "arr", "j", "j+1");

        graph.Add(program);
        graph.Add(arr);
        graph.Add(outerLoop);
        graph.Add(innerLoop);
        graph.Add(swapNode);

        graph.Connect(program.Id,  arr.Id,       Specc.Graph.EdgeType.Contains);
        graph.Connect(program.Id,  outerLoop.Id, Specc.Graph.EdgeType.Contains);
        graph.Connect(program.Id,  innerLoop.Id, Specc.Graph.EdgeType.Contains);
        graph.Connect(program.Id,  swapNode.Id,  Specc.Graph.EdgeType.Contains);

        return graph;
    }

    /// <summary>
    /// Builds the SelectionSort semantic graph directly (no LLM required).
    /// Array: [64, 25, 12, 22, 11, 90, 3, 45], sorted: [3, 11, 12, 22, 25, 45, 64, 90]
    /// </summary>
    public static Specc.Graph.SemanticGraph BuildSelectionSortGraph()
    {
        var graph    = new Specc.Graph.SemanticGraph();
        var program  = new Specc.Graph.ProgramNode(
            Guid.NewGuid(), "Program:SelectionSort", "SelectionSort");
        var arr      = new Specc.Graph.ArrayNode(
            Guid.NewGuid(), "Array:arr[8]", "arr", "int", 8,
            new[] { 64, 25, 12, 22, 11, 90, 3, 45 });
        var outerLoop = new Specc.Graph.LoopNode(
            Guid.NewGuid(), "Loop:i:0..6", 0, 6);
        var innerLoop = new Specc.Graph.NestedLoopNode(
            Guid.NewGuid(), "NestedLoop:j:i+1..7", "j", 0, "7");
        var minIndex  = new Specc.Graph.VariableNode(
            Guid.NewGuid(), "Var:min_index", "min_index", "int");
        var swapNode  = new Specc.Graph.SwapNode(
            Guid.NewGuid(), "Swap:arr[i]<->arr[min_index]", "arr", "i", "min_index");

        graph.Add(program);
        graph.Add(arr);
        graph.Add(outerLoop);
        graph.Add(innerLoop);
        graph.Add(minIndex);
        graph.Add(swapNode);

        graph.Connect(program.Id, arr.Id,       Specc.Graph.EdgeType.Contains);
        graph.Connect(program.Id, outerLoop.Id, Specc.Graph.EdgeType.Contains);
        graph.Connect(program.Id, innerLoop.Id, Specc.Graph.EdgeType.Contains);
        graph.Connect(program.Id, minIndex.Id,  Specc.Graph.EdgeType.Contains);
        graph.Connect(program.Id, swapNode.Id,  Specc.Graph.EdgeType.Contains);

        return graph;
    }

    /// <summary>
    /// Runs the BubbleSort graph through AcceptanceCriteria, CFG, StackIR, and MSIL passes.
    /// </summary>
    public static async Task<CompilationContext> AfterBubbleSortMsilAsync(string artifactsDir)
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = artifactsDir,
        };
        ctx.SemanticGraph = BuildBubbleSortGraph();
        await new AcceptanceCriteriaPass(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AcceptanceCriteriaPass>.Instance)
            .ExecuteAsync(ctx);
        await MakeCfgPass().ExecuteAsync(ctx);
        await MakeStackIrPass().ExecuteAsync(ctx);
        await MakeMsilGenerationPass().ExecuteAsync(ctx);
        return ctx;
    }
}
