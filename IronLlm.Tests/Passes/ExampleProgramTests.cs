using IronLlm.Graph;
using IronLlm.Passes;
using IronLlm.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronLlm.Tests.Passes;

/// <summary>
/// Compiles each example program from its .spec text through the full deterministic pipeline
/// (no Ollama) and verifies the binary output against expected lines.
/// These tests exercise pipeline generalisation — different loop bounds, divisors, and
/// branch counts — not just the FizzBuzz happy path.
/// </summary>
public class ExampleProgramTests : IDisposable
{
    private readonly string _artifactsDir;

    public ExampleProgramTests()
    {
        _artifactsDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_artifactsDir);
    }

    public void Dispose() => Directory.Delete(_artifactsDir, recursive: true);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<CompilationContext> CompileSpec(string specText)
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = _artifactsDir,
            RawSpec      = specText,
        };

        await PipelineFixtures.MakeSemanticGraphPass().ExecuteAsync(ctx);
        await new AcceptanceCriteriaPass(NullLogger<AcceptanceCriteriaPass>.Instance).ExecuteAsync(ctx);
        await PipelineFixtures.MakeCfgPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeStackIrPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeMsilGenerationPass().ExecuteAsync(ctx);
        await new AssemblyEmitPass(NullLogger<AssemblyEmitPass>.Instance).ExecuteAsync(ctx);
        return ctx;
    }

    private static async Task<string[]> RunAndGetLines(CompilationContext ctx)
    {
        var pass = new AcceptanceVerificationPass(NullLogger<AcceptanceVerificationPass>.Instance);
        await pass.ExecuteAsync(ctx);   // throws on failure; also populates ctx.VerificationOutput
        return ctx.VerificationOutput!;
    }

    private static bool IsExecutable(string path) =>
        !OperatingSystem.IsWindows() &&
        (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;

    private static async Task<string[]> RunBinaryAndGetLines(CompilationContext ctx)
    {
        var launcher = ctx.LauncherPath ?? ctx.AssemblyPath!;
        System.Diagnostics.ProcessStartInfo psi;
        if (System.IO.File.Exists(launcher) && IsExecutable(launcher))
            psi = new(launcher)            { RedirectStandardOutput = true, UseShellExecute = false };
        else
            psi = new("dotnet", launcher)  { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.TrimEnd('\r')).ToArray();
    }

    // ── Fizz (single branch, 30 iterations) ──────────────────────────────────

    private const string FizzSpec = """
        program: Fizz

        loop:
          from: 1
          to: 30

        branch:
          condition: divisible_by_3
          divisor: 3
          true_output: "Fizz"

        branch:
          condition: default
          true_output: "{n}"

        variable:
          name: n
          type: int
        """;

    [Fact]
    public async Task Fizz_Produces30Lines()
    {
        var ctx = await CompileSpec(FizzSpec);
        var lines = await RunAndGetLines(ctx);
        Assert.Equal(30, lines.Length);
    }

    [Fact]
    public async Task Fizz_MultiplesOf3_PrintFizz()
    {
        var ctx = await CompileSpec(FizzSpec);
        var lines = await RunAndGetLines(ctx);
        Assert.Equal("Fizz", lines[2]);   // iteration 3
        Assert.Equal("Fizz", lines[5]);   // iteration 6
        Assert.Equal("Fizz", lines[29]);  // iteration 30
    }

    [Fact]
    public async Task Fizz_NonMultiples_PrintNumber()
    {
        var ctx = await CompileSpec(FizzSpec);
        var lines = await RunAndGetLines(ctx);
        Assert.Equal("1",  lines[0]);
        Assert.Equal("2",  lines[1]);
        Assert.Equal("4",  lines[3]);
    }

    [Fact]
    public async Task Fizz_AcceptanceCriteria_Passes()
    {
        var ctx = await CompileSpec(FizzSpec);
        Assert.Equal(30, ctx.Assertions.Count);
    }

    // ── CountDown (no branches, 1–10) ─────────────────────────────────────────

    private const string CountDownSpec = """
        program: CountDown

        loop:
          from: 1
          to: 10

        branch:
          condition: default
          true_output: "{n}"

        variable:
          name: n
          type: int
        """;

    [Fact]
    public async Task CountDown_Produces10Lines()
    {
        var ctx = await CompileSpec(CountDownSpec);
        var lines = await RunAndGetLines(ctx);
        Assert.Equal(10, lines.Length);
    }

    [Fact]
    public async Task CountDown_PrintsNumbersInOrder()
    {
        var ctx = await CompileSpec(CountDownSpec);
        var lines = await RunAndGetLines(ctx);
        for (var i = 0; i < 10; i++)
            Assert.Equal((i + 1).ToString(), lines[i]);
    }

    // ── FizzBuzzHundred (two-divisor variant: 7 and 11) ───────────────────────

    private const string FizzBuzzHundredSpec = """
        program: FizzBuzzHundred

        loop:
          from: 1
          to: 100

        branch:
          condition: divisible_by_77
          divisor: 77
          true_output: "FizzBuzz"

        branch:
          condition: divisible_by_7
          divisor: 7
          true_output: "Fizz"

        branch:
          condition: divisible_by_11
          divisor: 11
          true_output: "Buzz"

        branch:
          condition: default
          true_output: "{n}"

        variable:
          name: n
          type: int
        """;

    [Fact]
    public async Task FizzBuzzHundred_Produces100Lines()
    {
        var ctx = await CompileSpec(FizzBuzzHundredSpec);
        var lines = await RunAndGetLines(ctx);
        Assert.Equal(100, lines.Length);
    }

    [Fact]
    public async Task FizzBuzzHundred_77_PrintsFizzBuzz()
    {
        var ctx = await CompileSpec(FizzBuzzHundredSpec);
        var lines = await RunAndGetLines(ctx);
        Assert.Equal("FizzBuzz", lines[76]);  // iteration 77
    }

    [Fact]
    public async Task FizzBuzzHundred_7_PrintsFizz()
    {
        var ctx = await CompileSpec(FizzBuzzHundredSpec);
        var lines = await RunAndGetLines(ctx);
        Assert.Equal("Fizz", lines[6]);   // iteration 7
        Assert.Equal("Fizz", lines[13]);  // iteration 14
    }

    [Fact]
    public async Task FizzBuzzHundred_11_PrintsBuzz()
    {
        var ctx = await CompileSpec(FizzBuzzHundredSpec);
        var lines = await RunAndGetLines(ctx);
        Assert.Equal("Buzz", lines[10]);  // iteration 11
        Assert.Equal("Buzz", lines[21]);  // iteration 22
    }

    [Fact]
    public async Task FizzBuzzHundred_AcceptanceCriteria_Passes()
    {
        var ctx = await CompileSpec(FizzBuzzHundredSpec);
        Assert.Equal(100, ctx.Assertions.Count);
    }

    // ── BubbleSort (array program, no spec text — build graph directly) ───────

    private async Task<CompilationContext> CompileBubbleSort()
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = _artifactsDir,
        };
        ctx.SemanticGraph = PipelineFixtures.BuildBubbleSortGraph();
        await new AcceptanceCriteriaPass(NullLogger<AcceptanceCriteriaPass>.Instance).ExecuteAsync(ctx);
        await PipelineFixtures.MakeCfgPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeStackIrPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeMsilGenerationPass().ExecuteAsync(ctx);
        await new AssemblyEmitPass(NullLogger<AssemblyEmitPass>.Instance).ExecuteAsync(ctx);
        return ctx;
    }

    private static async Task<string[]> RunBubbleSortAndGetLines(CompilationContext ctx)
    {
        var launcher = ctx.LauncherPath ?? ctx.AssemblyPath!;
        System.Diagnostics.ProcessStartInfo psi;
        if (System.IO.File.Exists(launcher) && IsExecutable(launcher))
        {
            psi = new System.Diagnostics.ProcessStartInfo(launcher)
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
            };
        }
        else
        {
            psi = new System.Diagnostics.ProcessStartInfo("dotnet", launcher)
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
            };
        }
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Fact]
    public async Task BubbleSort_Produces10Lines()
    {
        var ctx   = await CompileBubbleSort();
        var lines = await RunBubbleSortAndGetLines(ctx);
        Assert.Equal(10, lines.Length);
    }

    [Fact]
    public async Task BubbleSort_OutputIsSorted()
    {
        var ctx   = await CompileBubbleSort();
        var lines = await RunBubbleSortAndGetLines(ctx);
        var expected = new[] { "3", "11", "12", "22", "25", "34", "45", "64", "78", "90" };
        Assert.Equal(expected, lines);
    }

    // ── SelectionSort (array program, 8 elements) ─────────────────────────────

    private async Task<CompilationContext> CompileSelectionSort()
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = _artifactsDir,
        };
        ctx.SemanticGraph = PipelineFixtures.BuildSelectionSortGraph();
        await new AcceptanceCriteriaPass(NullLogger<AcceptanceCriteriaPass>.Instance).ExecuteAsync(ctx);
        await PipelineFixtures.MakeCfgPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeStackIrPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeMsilGenerationPass().ExecuteAsync(ctx);
        await new AssemblyEmitPass(NullLogger<AssemblyEmitPass>.Instance).ExecuteAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task SelectionSort_Produces8Lines()
    {
        var ctx   = await CompileSelectionSort();
        var lines = await RunBubbleSortAndGetLines(ctx);
        Assert.Equal(8, lines.Length);
    }

    [Fact]
    public async Task SelectionSort_OutputIsSorted()
    {
        var ctx   = await CompileSelectionSort();
        var lines = await RunBubbleSortAndGetLines(ctx);
        var expected = new[] { "3", "11", "12", "22", "25", "45", "64", "90" };
        Assert.Equal(expected, lines);
    }

    // ── Multiples (arithmetic: n * 7, 12 iterations) ─────────────────────────

    private const string MultiplesSpec = """
        program: Multiples

        loop:
          from: 1
          to: 12

        variable:
          name: n
          type: int

        variable:
          name: product
          type: int

        assign:
          target: product
          op: mul
          left: {n}
          right: 7

        branch:
          condition: default
          true_output: {product}
        """;

    [Fact]
    public async Task Multiples_Produces12Lines()
    {
        var ctx   = await CompileSpec(MultiplesSpec);
        var lines = await RunBinaryAndGetLines(ctx);
        Assert.Equal(12, lines.Length);
    }

    [Fact]
    public async Task Multiples_OutputIsCorrect()
    {
        var ctx   = await CompileSpec(MultiplesSpec);
        var lines = await RunBinaryAndGetLines(ctx);
        var expected = Enumerable.Range(1, 12).Select(i => (i * 7).ToString()).ToArray();
        Assert.Equal(expected, lines);
    }

    // ── Fibonacci (multi-assign loop, 10 iterations) ──────────────────────────

    private const string FibonacciSpec = """
        program: Fibonacci

        loop:
          from: 1
          to: 10

        variable:
          name: n
          type: int

        variable:
          name: a
          type: int
          initial_value: 1

        variable:
          name: b
          type: int
          initial_value: 0

        variable:
          name: tmp
          type: int

        assign:
          target: tmp
          op: copy
          left: {a}

        assign:
          target: a
          op: add
          left: {a}
          right: {b}

        assign:
          target: b
          op: copy
          left: {tmp}

        branch:
          condition: default
          true_output: {a}
        """;

    [Fact]
    public async Task Fibonacci_Produces10Lines()
    {
        var ctx   = await CompileSpec(FibonacciSpec);
        var lines = await RunBinaryAndGetLines(ctx);
        Assert.Equal(10, lines.Length);
    }

    [Fact]
    public async Task Fibonacci_OutputIsCorrect()
    {
        var ctx   = await CompileSpec(FibonacciSpec);
        var lines = await RunBinaryAndGetLines(ctx);
        var expected = new[] { "1", "1", "2", "3", "5", "8", "13", "21", "34", "55" };
        Assert.Equal(expected, lines);
    }

    // ── Guesser (int input, comparison branching) ─────────────────────────────

    private const string GuesserSpec = """
        program: Guesser

        variable:
          name: guess
          type: int
          source: stdin

        branch:
          condition: too_low
          compare: lt
          value: 42
          true_output: "Too low."

        branch:
          condition: too_high
          compare: gt
          value: 42
          true_output: "Too high."

        branch:
          condition: correct
          true_output: "Correct!"
        """;

    private async Task<CompilationContext> CompileGuesser(string input)
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = _artifactsDir,
            RawSpec      = GuesserSpec,
            TestInput    = input,
        };
        await PipelineFixtures.MakeSemanticGraphPass().ExecuteAsync(ctx);
        await new AcceptanceCriteriaPass(NullLogger<AcceptanceCriteriaPass>.Instance).ExecuteAsync(ctx);
        await PipelineFixtures.MakeCfgPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeStackIrPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeMsilGenerationPass().ExecuteAsync(ctx);
        await new AssemblyEmitPass(NullLogger<AssemblyEmitPass>.Instance).ExecuteAsync(ctx);
        return ctx;
    }

    private static async Task<string> RunGuesserAndGetOutput(CompilationContext ctx)
    {
        var launcher = ctx.LauncherPath ?? ctx.AssemblyPath!;
        System.Diagnostics.ProcessStartInfo psi;
        if (System.IO.File.Exists(launcher) && IsExecutable(launcher))
            psi = new(launcher) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        else
            psi = new("dotnet", launcher) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(ctx.TestInput);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Trim();
    }

    [Fact]
    public async Task Guesser_CorrectGuess_PrintsCorrect()
    {
        var ctx = await CompileGuesser("42");
        Assert.Equal("Correct!", await RunGuesserAndGetOutput(ctx));
    }

    [Fact]
    public async Task Guesser_LowGuess_PrintsTooLow()
    {
        var ctx = await CompileGuesser("10");
        Assert.Equal("Too low.", await RunGuesserAndGetOutput(ctx));
    }

    [Fact]
    public async Task Guesser_HighGuess_PrintsTooHigh()
    {
        var ctx = await CompileGuesser("99");
        Assert.Equal("Too high.", await RunGuesserAndGetOutput(ctx));
    }

    // ── Calculator (two int inputs, addition) ─────────────────────────────────

    private const string CalculatorSpec = """
        program: Calculator

        variable:
          name: a
          type: int
          source: stdin

        variable:
          name: b
          type: int
          source: stdin

        assign:
          target: sum
          op: add
          left: {a}
          right: {b}

        print: {sum}
        """;

    private async Task<CompilationContext> CompileCalculator(string input)
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = _artifactsDir,
            RawSpec      = CalculatorSpec,
            TestInput    = input,
        };
        await PipelineFixtures.MakeSemanticGraphPass().ExecuteAsync(ctx);
        await new AcceptanceCriteriaPass(NullLogger<AcceptanceCriteriaPass>.Instance).ExecuteAsync(ctx);
        await PipelineFixtures.MakeCfgPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeStackIrPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeMsilGenerationPass().ExecuteAsync(ctx);
        await new AssemblyEmitPass(NullLogger<AssemblyEmitPass>.Instance).ExecuteAsync(ctx);
        return ctx;
    }

    private static async Task<string> RunCalculatorAndGetOutput(CompilationContext ctx)
    {
        var launcher = ctx.LauncherPath ?? ctx.AssemblyPath!;
        System.Diagnostics.ProcessStartInfo psi;
        if (System.IO.File.Exists(launcher) && IsExecutable(launcher))
            psi = new(launcher) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        else
            psi = new("dotnet", launcher) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.StandardInput.WriteAsync(ctx.TestInput + "\n");
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Trim();
    }

    [Fact]
    public async Task Calculator_AddsTwoIntegers()
    {
        var ctx = await CompileCalculator("3\n7");
        Assert.Equal("10", await RunCalculatorAndGetOutput(ctx));
    }

    [Fact]
    public async Task Calculator_NegativeSum()
    {
        var ctx = await CompileCalculator("-5\n3");
        Assert.Equal("-2", await RunCalculatorAndGetOutput(ctx));
    }

    [Fact]
    public async Task Calculator_ZeroResult()
    {
        var ctx = await CompileCalculator("0\n0");
        Assert.Equal("0", await RunCalculatorAndGetOutput(ctx));
    }

    // ── Collatz (while loop, integer division, parity branching) ─────────────

    private const string CollatzSpec = """
        program: Collatz

        variable:
          name: n
          type: int
          source: stdin

        while:
          variable: n
          condition: ne
          value: 1

        print: {n}

        branch:
          condition: even
          divisor: 2
          true_assign:
            target: n
            op: div
            left: {n}
            right: 2

        branch:
          condition: odd
          true_assign:
            target: n
            op: mul
            left: {n}
            right: 3
          true_assign:
            target: n
            op: add
            left: {n}
            right: 1
        """;

    private async Task<CompilationContext> CompileCollatz(string input)
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "fake.spec",
            ArtifactsDir = _artifactsDir,
            RawSpec      = CollatzSpec,
            TestInput    = input,
            AuthorialAssertions = input == "6"
                ? new[] {"6","3","10","5","16","8","4","2","1"}
                    .Select((s, i) => new AssertionRecord(i + 1, s)).ToList()
                : new List<AssertionRecord>(),
        };
        await PipelineFixtures.MakeSemanticGraphPass().ExecuteAsync(ctx);
        await new AcceptanceCriteriaPass(NullLogger<AcceptanceCriteriaPass>.Instance).ExecuteAsync(ctx);
        await PipelineFixtures.MakeCfgPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeStackIrPass().ExecuteAsync(ctx);
        await PipelineFixtures.MakeMsilGenerationPass().ExecuteAsync(ctx);
        await new AssemblyEmitPass(NullLogger<AssemblyEmitPass>.Instance).ExecuteAsync(ctx);
        return ctx;
    }

    private static async Task<string[]> RunCollatzAndGetLines(CompilationContext ctx)
    {
        var launcher = ctx.LauncherPath ?? ctx.AssemblyPath!;
        System.Diagnostics.ProcessStartInfo psi;
        if (System.IO.File.Exists(launcher) && IsExecutable(launcher))
            psi = new(launcher) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        else
            psi = new("dotnet", launcher) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(ctx.TestInput);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Fact]
    public async Task Collatz_From6_Produces9Lines()
    {
        var ctx   = await CompileCollatz("6");
        var lines = await RunCollatzAndGetLines(ctx);
        Assert.Equal(9, lines.Length);
    }

    [Fact]
    public async Task Collatz_From6_CorrectSequence()
    {
        var ctx   = await CompileCollatz("6");
        var lines = await RunCollatzAndGetLines(ctx);
        Assert.Equal(["6","3","10","5","16","8","4","2","1"], lines);
    }

    [Fact]
    public async Task Collatz_From1_PrintsOnly1()
    {
        var ctx   = await CompileCollatz("1");
        var lines = await RunCollatzAndGetLines(ctx);
        Assert.Equal(["1"], lines);
    }
}
