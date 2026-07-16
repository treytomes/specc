namespace IronLlm.Passes;

public record AcceptanceFailure(int Iteration, string Expected, string Actual);

public class AcceptanceFailureException : CompilationException
{
    public IReadOnlyList<AcceptanceFailure> Failures { get; }

    public AcceptanceFailureException(IReadOnlyList<AcceptanceFailure> failures)
        : base(BuildMessage(failures))
    {
        Failures = failures;
    }

    public AcceptanceFailureException(string message) : base(message)
    {
        Failures = [];
    }

    private static string BuildMessage(IReadOnlyList<AcceptanceFailure> failures)
    {
        var lines = failures.Select(f =>
            $"  iteration {f.Iteration}: expected \"{f.Expected}\", got \"{f.Actual}\"");
        return $"Acceptance check failed ({failures.Count} assertion(s)):\n" +
               string.Join('\n', lines);
    }
}
