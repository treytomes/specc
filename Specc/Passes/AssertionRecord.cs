namespace Specc.Passes;

// IsSubstring: when true, the actual output line only needs to *contain* Expected,
// not equal it. Used for dynamic output lines (e.g. "Nice to meet you, Alice!")
// where the exact wording is known but the full line includes user input.
public record AssertionRecord(int Iteration, string Expected, bool IsSubstring = false);
