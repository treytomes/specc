# Spec 23 — SelectionSort Example Program

**Status:** Incomplete  
**Scope:** `examples/SelectionSort/` — new Markdown spec and expected artifacts; no compiler changes

## Motivation

BubbleSort is the only array example. It exercises `ArrayNode`, `NestedLoopNode`, `SwapNode`, and `IndexNode`, but only one algorithm shape. SelectionSort uses the same node types with a different inner-loop structure: the inner loop finds a minimum index rather than swapping on every comparison. A second working array example:

1. Confirms that the array/nested-loop lowering is not accidentally coupled to BubbleSort's specific swap pattern.
2. Provides a harder test for `CfgPass.LowerNestedLoop`: SelectionSort's inner loop reads and conditionally updates a `min_index` variable, requiring `Reads` and `Writes` edges to a variable node inside the nested loop body — a code path BubbleSort's unconditional swap doesn't exercise.
3. Gives the embedding pass a second array program so cosine similarity comparisons in `SemanticNormalizationPass` have more variety in the reference corpus.

## Program Description

SelectionSort sorts an array of 8 integers. For each position `i` from 0 to 6, it finds the index of the minimum element in the subarray from `i` to 7, then swaps the element at `i` with the minimum. The result is printed one element per line in ascending order.

## Example File

`examples/SelectionSort/SelectionSort.md`:

```markdown
# SelectionSort

Write a program called SelectionSort that sorts an array of 8 integers in place
using the selection sort algorithm.

Start with the array: 64 25 12 22 11 90 3 45

Use two nested loops. The outer loop runs from index 0 to 6 (inclusive).
The inner loop runs from index 1 plus the outer loop index to 7 (inclusive).

For each inner iteration, if the element at the current inner index is less than
the element at min_index, update min_index to the current inner index.
At the end of each outer iteration, swap the elements at positions i and min_index.

After sorting, print each element on its own line.

## Expected Output

After sorting, the program should print these 8 lines in order:

\```
3
11
12
22
25
45
64
90
\```
```

(Backslashes before code fences are escaping artifacts of this spec document — omit them in the actual file.)

## Node Types Required

All node types are already implemented:

| Node | Usage |
|------|-------|
| `ProgramNode` | root |
| `ArrayNode` | `values=[64,25,12,22,11,90,3,45]`, `size=8` |
| `VariableNode` | `min_index` (int), `i` (int) |
| `NestedLoopNode` | outer: `i` 0→6; inner: `i+1`→7 |
| `IndexNode` | read `arr[i]`, `arr[min_index]`, `arr[j]` |
| `SwapNode` | `arr[i]` ↔ `arr[min_index]` |
| `BranchNode` | `arr[j] < arr[min_index]` |
| `PrintNode` | `{arr[i]}` after sort |

No new node types are needed.

## Acceptance Criteria

1. `scripts/run.sh examples/SelectionSort` completes without error.
2. The compiled binary produces exactly the 8 lines in the `## Expected Output` block, in order.
3. `AcceptanceVerificationPass` reports 8/8 assertions passing.
4. All existing example programs (FizzBuzz, BubbleSort, CountDown) still pass.
5. All unit tests pass.

## Not In Scope

- Implementing in-place minimum tracking as a new node type — `VariableNode` + `BranchNode` are sufficient.
- Any changes to the compiler or test suite beyond creating the Markdown file.
