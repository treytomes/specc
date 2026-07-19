namespace Specc.Passes;

/// <summary>Records a single assertion mismatch: what was expected versus what the binary produced.</summary>
/// <param name="Iteration">Loop iteration or output-line index.</param>
/// <param name="Expected">The expected output string.</param>
/// <param name="Actual">The actual output string from the compiled binary.</param>
public record AcceptanceFailure(int Iteration, string Expected, string Actual);

/// <summary>Thrown by <see cref="AcceptanceVerificationPass"/> when one or more assertions fail.</summary>
public class AcceptanceFailureException : CompilationException
{
    /// <summary>Individual assertion mismatches that caused this exception.</summary>
    public IReadOnlyList<AcceptanceFailure> Failures { get; }

    /// <summary>Initialises the exception from a list of individual assertion failures.</summary>
    public AcceptanceFailureException(IReadOnlyList<AcceptanceFailure> failures)
        : base(BuildMessage(failures))
    {
        Failures = failures;
    }

    /// <summary>Initialises the exception with a free-form failure message and an empty failures list.</summary>
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
