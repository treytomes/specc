# Spec 05 — Second Spec: Bubble Sort

**Status:** Design — pipeline generalization required before implementation  
**Scope:** New `examples/BubbleSort/BubbleSort.md`, new node types, extensions to SemanticGraph/CFG/StackIR/MSIL passes

> **DEFERRED — not implemented yet.**
>
> BubbleSort requires new node types (ArrayNode, IndexNode, SwapNode), nested loop support, comparison-without-modulo, and extensions to nearly every pass. The current pipeline is specialized for the FizzBuzz family (single loop, modulo branches, scalar variable). This spec becomes actionable after a design conversation about how deep the generalization needs to go.
>
> **Replaced by:** This spec has been decomposed into Specs 15–19, which implement BubbleSort in two phases: narrow (graph layer, visualization, repository similarity — Specs 15–16) and wide (full compilation to a running binary — Specs 17–19). See those specs for implementation details. The graph repository integration is covered in Spec 16 once Spec 03 is complete.

## Motivation

FizzBuzz exercises a loop, modulo, branching, and print. Bubble sort exercises something FizzBuzz does not: a nested loop, comparison without modulo, and in-place array mutation. Running the pipeline against a second substantially different program:

- Tests that the semantic graph parser generalizes beyond one hard-coded structure.
- Exercises the graph repository: Loop nodes from FizzBuzz should appear as similar priors when compiling BubbleSort.
- Forces `StackIrPass` to handle patterns not yet present (array indexing, swap).

## Markdown Spec Draft

The primary input is a Markdown file that `MarkdownSpecPass` will extract a `.spec` from:

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

## New Node Types Required

The parser and graph will need:

- `ArrayNode` — name, element type, size, optional literal values
- `IndexNode` — array name, index expression
- `SwapNode` — two index expressions
- Nested loop support: either a `NestedLoopNode` or a `LoopBound` edge type connecting the outer loop to the inner loop's upper bound

## Why This Is the Right Second Program

The Loop nodes should cluster with the Loop nodes from FizzBuzz. The Comparison nodes should cluster differently from FizzBuzz's modulo-based branches. The graph visualization (`02b-semantic-graph.mmd`) will make this visible.

Verifying that two programs produce semantically related but structurally distinct graphs — and that the repository correctly surfaces FizzBuzz's Loop as a similar prior when compiling BubbleSort — is a concrete demonstration of the semantic graph accumulating meaning across compilations.

## Acceptance Criterion

`scripts/run.sh examples/BubbleSort/BubbleSort.md` completes all passes and produces a binary that prints the sorted array. The repository `index.json` then contains two entries. The graph visualization shows nested loop structure distinct from FizzBuzz's flat loop.
