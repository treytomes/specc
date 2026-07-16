# Spec 05 — Second Spec: Bubble Sort

**Status:** Ready to implement (after Spec 01)  
**Scope:** New `Spec/BubbleSort.spec`, validation of the pipeline against a second program

## Motivation

FizzBuzz exercises a loop, modulo, branching, and print. Bubble sort exercises something FizzBuzz doesn't: **a nested loop, comparison without modulo, and in-place array mutation**. Running the pipeline against a second substantially different program:

- Tests that the semantic graph parser generalises beyond one hard-coded structure.
- Exercises the graph repository: Loop nodes from FizzBuzz should appear as similar priors when compiling BubbleSort.
- Forces `StackIrPass` to handle patterns not yet present (array indexing, swap).

## Spec File Draft

```
program: BubbleSort

array:
  name: arr
  element_type: int
  size: 10
  values: 64 34 25 12 22 11 90 45 78 3

loop:
  name: outer
  variable: i
  from: 0
  to: 8     # n - 2

loop:
  name: inner
  variable: j
  from: 0
  to: outer_bound   # n - 1 - i

branch:
  condition: arr[j] > arr[j+1]
  true: swap arr[j] arr[j+1]

output:
  print arr
```

## New Node Types Required

The parser and graph will need:

- `ArrayNode` — name, element type, size, optional literal values
- `IndexNode` — array name, index expression  
- `SwapNode` — two index expressions
- `NestedLoopNode` or a `loop_bound` edge type connecting the outer loop to the inner loop's upper bound

## Why This Is the Right Second Program

From the conversation:

> The Loop nodes might cluster with the Loop nodes from FizzBuzz. The Comparison nodes might cluster.

Bubble sort has both — and adds array access, which FizzBuzz doesn't. The embedding space should show FizzBuzz's loop and BubbleSort's outer loop close together, while BubbleSort's comparison nodes cluster differently from FizzBuzz's modulo-based branches. Verifying this visually (or with a similarity report) is a concrete demonstration of the semantic graph accumulating meaning.

## Acceptance Criterion

`dotnet run Spec/BubbleSort.spec Artifacts/BubbleSort/` completes all six passes. The repository `index.json` then contains two entries. A `similarity-report` command (out of scope for this spec) can show which nodes cluster between the two programs.
