using Specc.Tests.Fixtures;

namespace Specc.Tests.Passes;

public class CfgPassTests
{
    private static readonly string[] ExpectedLabels =
    [
        "entry", "loop_test",
        "check_divisible_by_15", "check_divisible_by_3", "check_divisible_by_5",
        "print_divisible_by_15", "print_divisible_by_3", "print_divisible_by_5", "print_n",
        "loop_inc", "exit",
    ];

    private static Specc.Passes.CompilationContext BuildContext() => PipelineFixtures.AfterCfg();

    [Fact]
    public void CfgBlocks_IsNotEmpty()
    {
        Assert.NotEmpty(BuildContext().CfgBlocks);
    }

    [Fact]
    public void CfgBlocks_Count_IsEleven()
    {
        Assert.Equal(11, BuildContext().CfgBlocks.Count);
    }

    [Fact]
    public void CfgBlocks_ContainAllExpectedLabels()
    {
        var labels = BuildContext().CfgBlocks.Select(b => b.Label).ToHashSet();
        foreach (var expected in ExpectedLabels)
            Assert.Contains(expected, labels);
    }

    [Fact]
    public void EntryBlock_SuccessorTrue_Is_LoopTest()
    {
        var entry = BuildContext().CfgBlocks.Single(b => b.Label == "entry");
        Assert.Equal("loop_test", entry.SuccessorTrue);
        Assert.Null(entry.SuccessorFalse);
    }

    [Fact]
    public void LoopTestBlock_HasTwoSuccessors()
    {
        var loopTest = BuildContext().CfgBlocks.Single(b => b.Label == "loop_test");
        Assert.NotNull(loopTest.SuccessorTrue);
        Assert.NotNull(loopTest.SuccessorFalse);
    }

    [Fact]
    public void LoopTestBlock_FalseSuccessor_Is_Exit()
    {
        var loopTest = BuildContext().CfgBlocks.Single(b => b.Label == "loop_test");
        Assert.Equal("exit", loopTest.SuccessorFalse);
    }

    [Fact]
    public void PrintBlocks_AllPointTo_LoopInc()
    {
        var ctx = BuildContext();
        var printBlocks = ctx.CfgBlocks
            .Where(b => b.Label.StartsWith("print_"))
            .ToList();
        Assert.NotEmpty(printBlocks);
        foreach (var block in printBlocks)
            Assert.Equal("loop_inc", block.SuccessorTrue);
    }

    [Fact]
    public void LoopIncBlock_SuccessorTrue_Is_LoopTest()
    {
        var loopInc = BuildContext().CfgBlocks.Single(b => b.Label == "loop_inc");
        Assert.Equal("loop_test", loopInc.SuccessorTrue);
    }

    [Fact]
    public void ExitBlock_HasNoSuccessors()
    {
        var exit = BuildContext().CfgBlocks.Single(b => b.Label == "exit");
        Assert.Null(exit.SuccessorTrue);
        Assert.Null(exit.SuccessorFalse);
    }

    [Fact]
    public void AllSuccessors_ReferenceExistingLabels()
    {
        var ctx    = BuildContext();
        var labels = ctx.CfgBlocks.Select(b => b.Label).ToHashSet();
        foreach (var block in ctx.CfgBlocks)
        {
            if (block.SuccessorTrue  != null) Assert.Contains(block.SuccessorTrue,  labels);
            if (block.SuccessorFalse != null) Assert.Contains(block.SuccessorFalse, labels);
        }
    }

    [Fact]
    public void PrintFizzBuzzBlock_InstructionContains_FizzBuzz()
    {
        // The label is derived from the condition name; find the block with divisor 15's print output.
        var block = BuildContext().CfgBlocks
            .First(b => b.Label.StartsWith("print_") && b.Label != "print_n" &&
                        string.Join(" ", b.Instructions).Contains("FizzBuzz"));
        Assert.Contains("FizzBuzz", string.Join(" ", block.Instructions));
    }

    [Fact]
    public void EntryBlock_Instruction_InitialisesLoopVariable()
    {
        var entry = BuildContext().CfgBlocks.Single(b => b.Label == "entry");
        Assert.Single(entry.Instructions);
        Assert.Contains("1", entry.Instructions[0]);
    }

    [Fact]
    public async Task Execute_Throws_WhenSemanticGraphNotSet()
    {
        var ctx = PipelineFixtures.MakeContext();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            PipelineFixtures.MakeCfgPass().ExecuteAsync(ctx));
    }

    // ── Array program (BubbleSort) tests ─────────────────────────────────────

    private static async Task<Specc.Passes.CompilationContext> BuildArrayContext()
    {
        var graph = PipelineFixtures.BuildBubbleSortGraph();
        var ctx   = PipelineFixtures.MakeContext();
        ctx.SemanticGraph = graph;
        await PipelineFixtures.MakeCfgPass().ExecuteAsync(ctx);
        return ctx;
    }

    private static readonly string[] ExpectedArrayLabels =
    [
        "entry", "outer_loop_test", "inner_loop_init", "inner_loop_test",
        "compare", "swap", "inner_loop_inc", "outer_loop_inc",
        "print_init", "print_loop_test", "print_element", "print_inc", "exit",
    ];

    [Fact]
    public async Task Execute_ArrayProgram_ProducesExpectedBlocks()
    {
        var ctx    = await BuildArrayContext();
        var labels = ctx.CfgBlocks.Select(b => b.Label).ToHashSet();
        foreach (var expected in ExpectedArrayLabels)
            Assert.Contains(expected, labels);
    }

    [Fact]
    public async Task Execute_ArrayProgram_BlockCount_IsThirteen()
    {
        var ctx = await BuildArrayContext();
        Assert.Equal(13, ctx.CfgBlocks.Count);
    }

    [Fact]
    public async Task Execute_ArrayProgram_EntryBlockHasArrayInitInstructions()
    {
        var ctx   = await BuildArrayContext();
        var entry = ctx.CfgBlocks.Single(b => b.Label == "entry");
        // newarr + 10 element assignments + "i = 0"
        Assert.True(entry.Instructions.Count >= 12);
        Assert.Contains(entry.Instructions, i => i.StartsWith("newarr "));
        Assert.Contains(entry.Instructions, i => i.Contains("[0] ="));
        Assert.Contains(entry.Instructions, i => i == "i = 0");
    }

    [Fact]
    public async Task Execute_ArrayProgram_ExitBlockHasNoSuccessors()
    {
        var ctx  = await BuildArrayContext();
        var exit = ctx.CfgBlocks.Single(b => b.Label == "exit");
        Assert.Null(exit.SuccessorTrue);
        Assert.Null(exit.SuccessorFalse);
    }

    [Fact]
    public async Task Execute_ArrayProgram_AllSuccessors_ReferenceExistingLabels()
    {
        var ctx    = await BuildArrayContext();
        var labels = ctx.CfgBlocks.Select(b => b.Label).ToHashSet();
        foreach (var block in ctx.CfgBlocks)
        {
            if (block.SuccessorTrue  != null) Assert.Contains(block.SuccessorTrue,  labels);
            if (block.SuccessorFalse != null) Assert.Contains(block.SuccessorFalse, labels);
        }
    }
}
