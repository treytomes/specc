using IronLlm.Graph;
using IronLlm.Passes;

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
        Embedder     = NullEmbedder.Instance,
        ChatClient   = NullChatClient.Instance,
    };

    public static CompilationContext AfterParse()
    {
        var ctx = MakeContext();
        ctx.RawSpec = FizzBuzzSpecText;
        return ctx;
    }

    public static CompilationContext AfterGraph()
    {
        var ctx = AfterParse();
        new SemanticGraphPass().ExecuteAsync(ctx).GetAwaiter().GetResult();
        return ctx;
    }

    public static CompilationContext AfterCfg()
    {
        var ctx = AfterGraph();
        new CfgPass().ExecuteAsync(ctx).GetAwaiter().GetResult();
        return ctx;
    }

    public static CompilationContext AfterStackIr()
    {
        var ctx = AfterCfg();
        new StackIrPass().ExecuteAsync(ctx).GetAwaiter().GetResult();
        return ctx;
    }
}
