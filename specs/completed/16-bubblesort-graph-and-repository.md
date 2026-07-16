# Spec 16 — BubbleSort: Graph, Visualization, and Repository Similarity

**Status:** Ready to implement (after Spec 15)  
**Scope:** New `examples/BubbleSort/BubbleSort.md`, graph construction via `MarkdownSpecPass`, repository similarity check

## Motivation

This spec produces the first program in the IronLlm corpus whose semantic graph is structurally distinct from FizzBuzz's — nested loops, array access, comparison without modulo. It demonstrates the repository's core value: Loop nodes from BubbleSort should appear as similar priors to Loop nodes from FizzBuzz, while Comparison and Swap nodes have no FizzBuzz counterpart. The graph visualization makes this visible. The binary is not required to run; the pipeline stops after `SemanticNormalizationPass` (or optionally after `AcceptanceVerificationPass` if Spec 17–19 are complete).

## Markdown Spec

```markdown
# BubbleSort

Write a program called BubbleSort that sorts an array of 10 integers in place
using the bubble sort algorithm.

Start with the array: 64 34 25 12 22 11 90 45 78 3

Use two nested loops. The outer loop runs from index 0 to 8 (inclusive).
The inner loop runs from index 0 to (8 minus the outer loop index).

If the element at position j is greater than the element at position j+1,
swap them.

After sorting, print each element on its own line.
```

Write this to `examples/BubbleSort/BubbleSort.md`.

## Expected Graph Structure

`MarkdownSpecPass` should extract a graph containing at minimum:
- 1 `ProgramNode` (BubbleSort)
- 1 `ArrayNode` (name: `arr`, size: 10, values: [64,34,25,12,22,11,90,45,78,3])
- 1 `LoopNode` (outer: `i`, 0..8)
- 1 `NestedLoopNode` (inner: `j`, 0..`8-i`)
- 1 `ComparisonNode` (`arr[j] > arr[j+1]`)
- 2 `IndexNode`s (arr[j], arr[j+1])
- 1 `SwapNode` (arr[j] ↔ arr[j+1])
- 1 `PrintNode`

The exact node labels and edge types will vary by LLM output; the validation pass will enforce structural invariants.

## Repository Similarity Check

After a successful run:
1. `repository/index.json` should contain both FizzBuzz (or CountDown/Fizz) and BubbleSort entries.
2. Re-running `./iron-llm examples/BubbleSort` on a second pass should log at least one similar prior with similarity ≥ 0.85 — the outer `LoopNode` should match prior Loop nodes.
3. `ComparisonNode`, `SwapNode`, `IndexNode`, `ArrayNode` should have no strong priors in the FizzBuzz family (similarity < 0.85), demonstrating that the embedding space distinguishes program families.

## Acceptance Criterion

`./iron-llm examples/BubbleSort` completes through `SemanticNormalizationPass` without error. The artifacts directory contains `02-semantic-graph.json`, `02c-semantic-graph.svg`, and `03-embeddings.json`. The repository `index.json` contains a BubbleSort entry. A second run logs at least one prior with similarity ≥ 0.85.

The pipeline may fail at `CfgPass` or later — that is expected until Spec 17–19 are implemented. Use `--force 04` (or stop the pipeline at pass 03b) to validate the graph layer in isolation.

## Not in Scope

- CFG lowering for arrays or nested loops (Spec 17)
- StackIR array opcodes (Spec 18)
- Running BubbleSort binary or verifying sorted output (Spec 19)
