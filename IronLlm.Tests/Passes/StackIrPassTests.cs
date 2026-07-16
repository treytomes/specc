using IronLlm.Graph;
using IronLlm.Tests.Fixtures;

namespace IronLlm.Tests.Passes;

public class StackIrPassTests
{
    private static IronLlm.Passes.CompilationContext BuildContext() => PipelineFixtures.AfterStackIr();

    [Fact]
    public void StackIr_IsNotEmpty()
    {
        Assert.NotEmpty(BuildContext().StackIr);
    }

    [Fact]
    public void StackIr_ContainsLdcI4_ForLoopInit()
    {
        var ir = BuildContext().StackIr;
        Assert.Contains(ir, i => i.Op == OpCode.LdcI4 && i.Operand == "1");
    }

    [Fact]
    public void StackIr_ContainsRem_ForModuloChecks()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.Rem);
    }

    [Fact]
    public void StackIr_ContainsLdstrS_ForStringLiterals()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.LdstrS);
    }

    [Fact]
    public void StackIr_ContainsCall_ForConsoleWriteLine()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.Call);
    }

    [Fact]
    public void StackIr_ContainsRet()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.Ret);
    }

    [Fact]
    public async Task StackIr_ContainsLabel_ForEachCfgBlock()
    {
        var ctx    = PipelineFixtures.AfterCfg();
        var labels = ctx.CfgBlocks.Select(b => b.Label).ToHashSet();
        await PipelineFixtures.MakeStackIrPass().ExecuteAsync(ctx);
        var irLabels = ctx.StackIr
            .Where(i => i.Op == OpCode.Label)
            .Select(i => i.Operand!)
            .ToHashSet();
        foreach (var label in labels)
            Assert.Contains(label, irLabels);
    }

    [Fact]
    public void StackIr_OpsWithRequiredOperands_HaveNonNullOperand()
    {
        var requiresOperand = new[] { OpCode.LdcI4, OpCode.LdstrS, OpCode.Label, OpCode.Br, OpCode.Brfalse, OpCode.Brtrue };
        foreach (var instr in BuildContext().StackIr)
            if (requiresOperand.Contains(instr.Op))
                Assert.NotNull(instr.Operand);
    }

    [Fact]
    public void StackIr_ContainsCgt_ForLoopTest()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.Cgt);
    }

    [Fact]
    public void StackIr_ContainsBrtrue_ForLoopExitBranch()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.Brtrue && i.Operand == "exit");
    }

    [Fact]
    public void StackIr_ContainsCeq_ForDivisibilityChecks()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.Ceq);
    }

    [Fact]
    public void StackIr_ContainsAdd_ForLoopIncrement()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.Add);
    }

    [Fact]
    public void StackIr_ContainsLdstrS_FizzBuzz()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.LdstrS && i.Operand == "FizzBuzz");
    }

    [Fact]
    public void StackIr_ContainsLdstrS_Fizz()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.LdstrS && i.Operand == "Fizz");
    }

    [Fact]
    public void StackIr_ContainsLdstrS_Buzz()
    {
        Assert.Contains(BuildContext().StackIr, i => i.Op == OpCode.LdstrS && i.Operand == "Buzz");
    }

    // ── Pattern coverage — verify each CfgPass instruction string lowers correctly ──

    [Theory]
    [InlineData("n = 1",                  OpCode.LdcI4,  "1")]
    [InlineData("n = 1",                  OpCode.StlocS, "n")]
    [InlineData("n = 42",                 OpCode.LdcI4,  "42")]
    [InlineData("if n > 100 goto exit",   OpCode.LdlocS, "n")]
    [InlineData("if n > 100 goto exit",   OpCode.LdcI4,  "100")]
    [InlineData("if n > 100 goto exit",   OpCode.Cgt,    null)]
    [InlineData("if n % 15 == 0",         OpCode.LdcI4,  "15")]
    [InlineData("if n % 15 == 0",         OpCode.Rem,    null)]
    [InlineData("if n % 15 == 0",         OpCode.Ceq,    null)]
    [InlineData("print n",                OpCode.LdlocS, "n")]
    [InlineData("print \"Fizz\"",         OpCode.LdstrS, "Fizz")]
    [InlineData("n = n + 1",              OpCode.Add,    null)]
    [InlineData("n = n + 1",              OpCode.StlocS, "n")]
    public void LowerInstruction_ProducesExpectedOp(string instr, OpCode expectedOp, string? expectedOperand)
    {
        var pass    = PipelineFixtures.MakeStackIrPass();
        var lowered = InvokeLower(pass, instr).ToList();
        Assert.Contains(lowered, op =>
            op.Op == expectedOp &&
            (expectedOperand == null || op.Operand == expectedOperand));
    }

    [Fact]
    public void LowerInstruction_LoopInit_DoesNotMatchIncrement()
    {
        var pass = PipelineFixtures.MakeStackIrPass();
        // "n = n + 1" must not be treated as init
        var ops = InvokeLower(pass, "n = n + 1").ToList();
        Assert.DoesNotContain(ops, op => op.Op == OpCode.LdcI4 && op.Operand == "n");
    }

    [Fact]
    public void LowerInstruction_UnrecognisedInstruction_ReturnsEmpty()
    {
        var pass = PipelineFixtures.MakeStackIrPass();
        var ops  = InvokeLower(pass, "XYZZY unknown instruction").ToList();
        Assert.Empty(ops);
    }

    // Reflection helper — LowerInstruction is private.
    private static IEnumerable<StackInstruction> InvokeLower(
        IronLlm.Passes.StackIrPass pass, string instr)
    {
        var method = typeof(IronLlm.Passes.StackIrPass)
            .GetMethod("LowerInstruction",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (IEnumerable<StackInstruction>)method.Invoke(null, [instr])!;
    }

    [Fact]
    public void Execute_Throws_WhenCfgBlocksEmpty()
    {
        var ctx = PipelineFixtures.MakeContext();
        Assert.ThrowsAny<Exception>(() =>
            PipelineFixtures.MakeStackIrPass().ExecuteAsync(ctx).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Execute_EmitsWarning_ForUnrecognisedInstruction()
    {
        var logger = new IronLlm.Tests.Fixtures.FakeLogger<IronLlm.Passes.StackIrPass>();
        var pass   = new IronLlm.Passes.StackIrPass(logger);
        var ctx    = PipelineFixtures.AfterCfg();

        // Inject an unrecognisable instruction into the first block.
        ctx.CfgBlocks[0].Instructions.Add("XYZZY do something unknown");

        await pass.ExecuteAsync(ctx);

        Assert.Contains(logger.Records,
            r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                 r.Message.Contains("unrecognised"));
    }
}
