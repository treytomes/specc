using IronLlm.Graph;
using IronLlm.Passes;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronLlm.Tests.Fixtures;

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
}
