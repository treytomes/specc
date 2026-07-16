using IronLlm.Passes;
using IronLlm.Tests.Fixtures;

namespace IronLlm.Tests.Passes;

public class CfgPassTests
{
    private static readonly string[] ExpectedLabels =
    [
        "entry", "loop_test",
        "fizzbuzz_check", "fizz_check", "buzz_check",
        "print_fizzbuzz", "print_fizz", "print_buzz", "print_n",
        "loop_inc", "exit",
    ];

    private static IronLlm.Passes.CompilationContext BuildContext() => PipelineFixtures.AfterCfg();

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
        foreach (var label in new[] { "print_fizzbuzz", "print_fizz", "print_buzz", "print_n" })
        {
            var block = ctx.CfgBlocks.Single(b => b.Label == label);
            Assert.Equal("loop_inc", block.SuccessorTrue);
        }
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
        var block = BuildContext().CfgBlocks.Single(b => b.Label == "print_fizzbuzz");
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
        await Assert.ThrowsAnyAsync<Exception>(() => new CfgPass().ExecuteAsync(ctx));
    }
}
