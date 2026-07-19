using Specc.Graph;
using Specc.Tests.Fixtures;

namespace Specc.Tests.Passes;

public class StackIrPassTests
{
    private static Specc.Passes.CompilationContext BuildContext() => PipelineFixtures.AfterStackIr();

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
    public void StackIr_ContainsIntrinsic_ForConsoleWriteLine()
    {
        Assert.Contains(BuildContext().StackIr,
            i => i.Op == OpCode.Intrinsic && i.Operand!.StartsWith("console.write_line"));
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
        Specc.Passes.StackIrPass pass, string instr)
    {
        var method = typeof(Specc.Passes.StackIrPass)
            .GetMethod("LowerInstruction",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (IEnumerable<StackInstruction>)method.Invoke(null, [instr, null])!;
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
        var logger = new Specc.Tests.Fixtures.FakeLogger<Specc.Passes.StackIrPass>();
        var pass   = new Specc.Passes.StackIrPass(logger);
        var ctx    = PipelineFixtures.AfterCfg();

        // Inject an unrecognisable instruction into the first block.
        ctx.CfgBlocks[0].Instructions.Add("XYZZY do something unknown");

        await pass.ExecuteAsync(ctx);

        Assert.Contains(logger.Records,
            r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                 r.Message.Contains("unrecognised"));
    }

    // ── New opcode / pattern tests ────────────────────────────────────────────

    [Theory]
    // Array element init: arr[0] = 64
    [InlineData("arr[0] = 64",                          OpCode.LdlocA,   "arr")]
    [InlineData("arr[0] = 64",                          OpCode.LdcI4,    "0")]
    [InlineData("arr[0] = 64",                          OpCode.LdcI4,    "64")]
    [InlineData("arr[0] = 64",                          OpCode.StelemI4, null)]
    // Dynamic inner loop bound: if j > (8 - i) goto outer_loop_inc
    [InlineData("if j > (8 - i) goto outer_loop_inc",  OpCode.LdlocS,   "j")]
    [InlineData("if j > (8 - i) goto outer_loop_inc",  OpCode.LdcI4,    "8")]
    [InlineData("if j > (8 - i) goto outer_loop_inc",  OpCode.LdlocS,   "i")]
    [InlineData("if j > (8 - i) goto outer_loop_inc",  OpCode.Sub,      null)]
    [InlineData("if j > (8 - i) goto outer_loop_inc",  OpCode.Cgt,      null)]
    // Array comparison: if arr[j] > arr[j+1]
    [InlineData("if arr[j] > arr[j+1]",                OpCode.LdlocA,   "arr")]
    [InlineData("if arr[j] > arr[j+1]",                OpCode.LdlocS,   "j")]
    [InlineData("if arr[j] > arr[j+1]",                OpCode.LdelemI4, null)]
    [InlineData("if arr[j] > arr[j+1]",                OpCode.LdcI4,    "1")]
    [InlineData("if arr[j] > arr[j+1]",                OpCode.Add,      null)]
    [InlineData("if arr[j] > arr[j+1]",                OpCode.Cgt,      null)]
    // Print array element: print arr[k]
    [InlineData("print arr[k]",                        OpCode.LdlocA,   "arr")]
    [InlineData("print arr[k]",                        OpCode.LdlocS,   "k")]
    [InlineData("print arr[k]",                        OpCode.LdelemI4, null)]
    [InlineData("print arr[k]",                        OpCode.Intrinsic, "console.write_line.int")]
    public void LowerInstruction_NewPatterns_ProducesExpectedOp(
        string instr, OpCode expectedOp, string? expectedOperand)
    {
        var pass    = PipelineFixtures.MakeStackIrPass();
        var lowered = InvokeLower(pass, instr).ToList();
        Assert.Contains(lowered, op =>
            op.Op == expectedOp &&
            (expectedOperand == null || op.Operand == expectedOperand));
    }

    [Fact]
    public void LowerInstruction_ArrInit_EmitsFourInstructions()
    {
        var pass = PipelineFixtures.MakeStackIrPass();
        var ops  = InvokeLower(pass, "arr[3] = 99").ToList();
        Assert.Equal(4, ops.Count);
        Assert.Equal(OpCode.LdlocA,   ops[0].Op);
        Assert.Equal(OpCode.LdcI4,    ops[1].Op);
        Assert.Equal(OpCode.LdcI4,    ops[2].Op);
        Assert.Equal(OpCode.StelemI4, ops[3].Op);
    }

    [Fact]
    public void LowerInstruction_Swap_EmitsExpectedSequence()
    {
        var pass = PipelineFixtures.MakeStackIrPass();
        var ops  = InvokeLower(pass, "swap arr[j] arr[j+1]").ToList();

        // temp = arr[j]: LdlocA, LdlocS(j), LdelemI4, StlocS(temp)
        Assert.Equal(OpCode.LdlocA,   ops[0].Op);
        Assert.Equal(OpCode.LdlocS,   ops[1].Op); Assert.Equal("j",    ops[1].Operand);
        Assert.Equal(OpCode.LdelemI4, ops[2].Op);
        Assert.Equal(OpCode.StlocS,   ops[3].Op); Assert.Equal("temp", ops[3].Operand);

        // arr[j] = arr[j+1]: LdlocA, LdlocS(j), LdlocA, LdlocS(j), LdcI4(1), Add, LdelemI4, StelemI4
        Assert.Equal(OpCode.LdlocA,   ops[4].Op);
        Assert.Equal(OpCode.LdlocS,   ops[5].Op);
        Assert.Equal(OpCode.LdlocA,   ops[6].Op);
        Assert.Equal(OpCode.LdlocS,   ops[7].Op);
        Assert.Equal(OpCode.LdcI4,    ops[8].Op);  Assert.Equal("1", ops[8].Operand);
        Assert.Equal(OpCode.Add,      ops[9].Op);
        Assert.Equal(OpCode.LdelemI4, ops[10].Op);
        Assert.Equal(OpCode.StelemI4, ops[11].Op);

        // arr[j+1] = temp: LdlocA, LdlocS(j), LdcI4(1), Add, LdlocS(temp), StelemI4
        Assert.Equal(OpCode.LdlocA,   ops[12].Op);
        Assert.Equal(OpCode.LdlocS,   ops[13].Op);
        Assert.Equal(OpCode.LdcI4,    ops[14].Op); Assert.Equal("1", ops[14].Operand);
        Assert.Equal(OpCode.Add,      ops[15].Op);
        Assert.Equal(OpCode.LdlocS,   ops[16].Op); Assert.Equal("temp", ops[16].Operand);
        Assert.Equal(OpCode.StelemI4, ops[17].Op);
    }

    [Fact]
    public void LowerInstruction_Newarr_EmitsThreeInstructions()
    {
        var pass = PipelineFixtures.MakeStackIrPass();
        var ops  = InvokeLower(pass, "newarr arr 10").ToList();
        Assert.Equal(3, ops.Count);
        Assert.Equal(OpCode.LdcI4,  ops[0].Op); Assert.Equal("10",  ops[0].Operand);
        Assert.Equal(OpCode.Newarr, ops[1].Op);
        Assert.Equal(OpCode.StlocA, ops[2].Op); Assert.Equal("arr", ops[2].Operand);
    }
}
