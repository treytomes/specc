namespace Specc.Passes;

// IsSubstring: when true, the actual output line only needs to *contain* Expected,
// not equal it. Used for dynamic output lines (e.g. "Nice to meet you, Alice!")
// where the exact wording is known but the full line includes user input.
/// <summary>A single acceptance assertion: an expected output line for a given iteration.</summary>
/// <param name="Iteration">Loop iteration or line index this assertion applies to.</param>
/// <param name="Expected">Expected output string.</param>
/// <param name="IsSubstring">When true, the actual output only needs to contain <paramref name="Expected"/> rather than equal it.</param>
public record AssertionRecord(int Iteration, string Expected, bool IsSubstring = false);
