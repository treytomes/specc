# Spec 24 — Multiples Example Program

**Status:** Completed  
**Scope:** `examples/Multiples/` — 12/12 assertions pass end-to-end

## Motivation

All current loop/branch examples (FizzBuzz, FizzBuzzHundred, Fizz, CountDown) produce string labels or raw numbers. None of them produce computed numeric output where the printed value is a mathematical function of the loop counter rather than a fixed label. A "Multiples" program — print every multiple of N from 1 to M — tests the `{variable}` print template path with a non-trivial transform between the counter and the output. Concretely, it confirms that `PrintNode.Template = "{product}"` resolves correctly when `product` is derived from a `VariableNode` holding a running product, not just the raw loop counter.

It also confirms the pipeline handles a program with no divisibility branches at all — only a default branch — which is structurally distinct from FizzBuzz and is worth having a regression test for.

## Program Description

Print the first 12 multiples of 7 (i.e., 7, 14, 21, ... 84), one per line. The loop counter runs from 1 to 12. Each iteration prints `n * 7`.

This is expressible in the existing `.spec` format as a single-branch program where `true_output` is `{product}` and a `VariableNode` for `product` carries the value `n * 7`. The graph uses a `Constant` node for the multiplier 7 and a `Writes` edge from the loop body to the variable.

## Example File

`examples/Multiples/Multiples.md`:

```markdown
# Multiples

Write a program called Multiples that prints the first 12 multiples of 7.

Iterate from 1 to 12. On each iteration, multiply the counter by 7
and print the result.

The loop counter is an integer variable named `n`.
The product is an integer variable named `product`.

## Expected Output

\```
7
14
21
28
35
42
49
56
63
70
77
84
\```
```

(Backslashes before code fences are escaping artifacts of this spec document — omit them in the actual file.)

## Node Types Required

| Node | Usage |
|------|-------|
| `ProgramNode` | root |
| `LoopNode` | 1→12 |
| `VariableNode` | `n` (int), `product` (int) |
| `ConstantNode` | value=7 |
| `BranchNode` | default branch (no divisor) |
| `PrintNode` | `{product}` |

No new node types are needed.

## Acceptance Criteria

1. `scripts/run.sh examples/Multiples/Multiples.md` completes without error. ✓
2. The compiled binary prints exactly the 12 lines in the `## Expected Output` block. ✓
3. `AcceptanceVerificationPass` reports 12/12 assertions passing. ✓
4. All existing example programs still pass. ✓
5. All unit tests pass (278/278). ✓

## Not In Scope

- Any arithmetic evaluation node — `product` is a derived variable; the graph may represent the computation as a `ConstantNode` with a `Reads` edge to the print node, leaving the actual multiplication to the `MsilGenerationPass` interpreter. If that requires a new `MultiplyNode`, that becomes a separate spec.
- Generalizing to arbitrary multipliers — this is a fixed program, not a parameterized one.

## Note on Pipeline Feasibility

If `SemanticGraphPass` or `StackIrPass` cannot yet represent a variable whose value is `n * 7` (because the current graph types only support print templates that reference the loop counter directly), this spec documents the desired end state and the gap becomes a separate spec for arithmetic variable nodes. The acceptance criterion 1 would then fail with a compilation error, which is the correct signal. Do not paper over the gap with a workaround in the Markdown — the spec should reflect the intended program.
