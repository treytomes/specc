# Spec 19 — BubbleSort: Full Pipeline and Acceptance Verification

**Status:** Ready to implement (after Spec 18)  
**Scope:** `AcceptanceCriteriaPass.cs`, `MarkdownSpecPass.cs` (authorial criteria), `examples/BubbleSort/`

## Motivation

With Specs 15–18 in place, BubbleSort should compile to a running binary. This spec closes the loop: define the acceptance criteria (sorted output), verify the binary, and confirm the repository contains a complete BubbleSort entry including the compiled artifact paths.

## Acceptance Criteria

BubbleSort's output is deterministic: sorting `[64, 34, 25, 12, 22, 11, 90, 45, 78, 3]` produces `[3, 11, 12, 22, 25, 34, 45, 64, 78, 90]`.

Expected stdout (10 lines):
```
3
11
12
22
25
34
45
64
78
90
```

### AcceptanceCriteriaPass

The current `AcceptanceCriteriaPass` derives expected output from graph-structure (loop bounds + divisors). For BubbleSort, the output depends on runtime sort behavior, not graph structure — it cannot be derived deterministically from the graph alone.

`AcceptanceCriteriaPass` should detect the presence of an `ArrayNode` and skip graph-derived assertion generation, leaving `context.Assertions` empty. `AcceptanceVerificationPass` will then fall back to authorial assertions (from `MarkdownSpecPass`).

### AuthorialAssertions

`MarkdownSpecPass` already makes a second LLM call to extract authorial criteria from the Markdown prose. The BubbleSort spec includes the initial array and the sort operation — the LLM should be able to derive the 10 expected output lines from that. No changes to `MarkdownSpecPass` are required; the existing authorial criteria extraction should handle BubbleSort.

If the LLM fails to produce correct criteria, add the expected output explicitly to `BubbleSort.md`:

```markdown
## Expected Output

After sorting, the program should print these 10 lines in order:
3, 11, 12, 22, 25, 34, 45, 64, 78, 90
```

## End-to-End Test

Add `BubbleSort_FullPipeline` to `ExampleProgramTests.cs` (spec text path, no Ollama):
- Compile through all passes using a hardcoded spec (constructed programmatically with the new node types).
- Run the binary.
- Assert stdout equals the 10 expected lines in order.

## Repository Verification

After a successful run:
- `repository/index.json` contains a BubbleSort entry with all 5 artifact paths populated.
- A second run of `./iron-llm examples/BubbleSort` logs at least one similar prior at ≥ 0.85 from a prior FizzBuzz-family compilation.

## Acceptance Criterion

`./iron-llm examples/BubbleSort` completes all passes, binary runs, stdout matches the 10 expected sorted lines, `08-AcceptanceVerification` logs `10/10 assertions passed`.
