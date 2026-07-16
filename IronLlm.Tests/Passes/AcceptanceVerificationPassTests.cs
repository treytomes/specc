using IronLlm.Passes;
using IronLlm.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronLlm.Tests.Passes;

/// <summary>
/// Compiles the real FizzBuzz binary and runs AcceptanceVerificationPass against it.
/// No stubs — the same code path as production.
/// </summary>
public class AcceptanceVerificationPassTests : IDisposable
{
    private readonly string _artifactsDir;
    private readonly CompilationContext _ctx;

    public AcceptanceVerificationPassTests()
    {
        _artifactsDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_artifactsDir);

        // Build a complete compiled context: StackIR → assembly → acceptance criteria.
        _ctx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = _artifactsDir,
        };

        // Populate context up through StackIR using fixtures.
        var stackCtx = PipelineFixtures.AfterStackIr();
        _ctx.SemanticGraph = stackCtx.SemanticGraph;
        _ctx.CfgBlocks     = stackCtx.CfgBlocks;
        _ctx.StackIr       = stackCtx.StackIr;

        // Emit the real binary.
        var emitPass = new AssemblyEmitPass(NullLogger<AssemblyEmitPass>.Instance);
        emitPass.ExecuteAsync(_ctx).GetAwaiter().GetResult();

        // Derive acceptance criteria from the graph.
        var criteriaPass = new AcceptanceCriteriaPass(NullLogger<AcceptanceCriteriaPass>.Instance);
        criteriaPass.ExecuteAsync(_ctx).GetAwaiter().GetResult();
    }

    public void Dispose() => Directory.Delete(_artifactsDir, recursive: true);

    private static AcceptanceVerificationPass MakePass() =>
        new(NullLogger<AcceptanceVerificationPass>.Instance);

    [Fact]
    public async Task Execute_Passes_WhenBinaryOutputMatchesAssertions()
    {
        // Should complete without throwing.
        await MakePass().ExecuteAsync(_ctx);
    }

    [Fact]
    public async Task Execute_Throws_AcceptanceFailureException_WhenExpectedValueIsWrong()
    {
        // Corrupt one assertion so the real output won't match.
        var corrupted = _ctx.Assertions.ToList();
        corrupted[14] = new AssertionRecord(15, "WRONG");   // iteration 15 should be "FizzBuzz"
        _ctx.Assertions = corrupted;

        var ex = await Assert.ThrowsAsync<AcceptanceFailureException>(() =>
            MakePass().ExecuteAsync(_ctx));

        Assert.Contains(ex.Failures, f => f.Iteration == 15 && f.Expected == "WRONG");
    }

    [Fact]
    public async Task Execute_Throws_WhenAssertionCountDoesNotMatchOutputLines()
    {
        // Add an extra assertion so count diverges from real output.
        _ctx.Assertions = [.._ctx.Assertions, new AssertionRecord(101, "extra")];

        await Assert.ThrowsAsync<AcceptanceFailureException>(() =>
            MakePass().ExecuteAsync(_ctx));
    }

    [Fact]
    public async Task Execute_IsNoOp_WhenAssertionsEmpty()
    {
        _ctx.Assertions = [];
        // Must not throw even though there is no launcher to run.
        var emptyCtx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = _artifactsDir,
            Assertions   = [],
        };
        await MakePass().ExecuteAsync(emptyCtx);
    }

    [Fact]
    public async Task Execute_PrefersAuthorialAssertions_OverGraphDerived()
    {
        // AuthorialAssertions = correct values; Assertions = all wrong.
        // If the pass uses Assertions it throws; if it uses AuthorialAssertions it passes.
        var correct = _ctx.Assertions.ToList();
        _ctx.AuthorialAssertions = correct;
        _ctx.Assertions = correct.Select(a => new AssertionRecord(a.Iteration, "WRONG")).ToList();

        await MakePass().ExecuteAsync(_ctx);
    }
}
