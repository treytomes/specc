# Spec 25 — Fibonacci Example Program

**Status:** Completed  
**Scope:** `examples/Fibonacci/` — 10/10 assertions pass end-to-end

## Motivation

Fibonacci is the canonical example of a program whose output depends on two prior values — `fib(n) = fib(n-1) + fib(n-2)`. It requires carrying two mutable variables across loop iterations and performing integer addition, which exercises the graph and lowering in a way that no current example does. All existing programs either print a static string label, the raw loop counter, or a sorted array element. None produce a value computed by adding two variables.

A working Fibonacci example would confirm:

1. `VariableNode` can represent `a`, `b`, and `tmp` across iterations.
2. The graph can express `tmp = a`, `a = a + b`, `b = tmp` as a sequence of assignments.
3. `CfgPass` and `StackIrPass` lower the assignment sequence correctly.
4. The embedding pass produces semantically distinct vectors for `a`, `b`, `tmp` — a meaningful diversity test for `SemanticNormalizationPass`.

## Program Description

Print the first 10 Fibonacci numbers (1, 1, 2, 3, 5, 8, 13, 21, 34, 55), one per line. Use `a = 1`, `b = 0`, and a temporary variable `tmp`. On each iteration: print `a`, then `tmp = a` (copy), `a = a + b`, `b = tmp` (copy). Initial values: `a = 1`, `b = 0`.

## Example File

`examples/Fibonacci/Fibonacci.md` (current content):

```markdown
# Fibonacci

Write a program called Fibonacci.

Use an integer loop counter `n` that runs from 1 to 10.
Use two integer variables `a` and `b`. Set `a` to 1 and `b` to 0 before the loop starts.
Use a temporary integer variable `tmp`.

On each iteration of the loop, perform these steps in order:
1. Print the current value of `a`.
2. Copy the value of `a` into `tmp`.
3. Set `a` to `a + b`.
4. Set `b` to the value of `tmp`.

## Expected Output

```
1
1
2
3
5
8
13
21
34
55
```
```

The description avoids naming the algorithm ("Fibonacci numbers") and describes the steps purely procedurally. This is necessary because ministral-3b may not reliably recall the sequence from the name alone (see Spec 30).

## Node Types Required

| Node | Exists? | Notes |
|------|---------|-------|
| `ProgramNode` | yes | root |
| `LoopNode` | yes | 1→10 |
| `VariableNode` | yes | `a`, `b`, `tmp` (int) |
| `ConstantNode` | yes | initial values 1, 1 |
| `PrintNode` | yes | `{a}` |
| `AddNode` | **no** | `a + b` → new node type |
| `AssignNode` | **no** | `a = expr` → new node type; or reuse `VariableNode` with a `Writes` edge |

## Feasibility Note

The assignment sequence `tmp = a; a = a + b; b = tmp` requires expressing:
- An addition of two variable values
- Assignment of an expression result to a variable

The current type system has `VariableNode` (holds a name and type) and `ConstantNode` (holds a literal int), but no arithmetic or assignment nodes. Two paths:

**Path A — New `ArithmeticNode` and `AssignNode` types**: Add `ArithmeticNode(op: "add" | "sub" | "mul", left: expr, right: expr)` and `AssignNode(target: varName, source: expr)`. This is the clean path but requires new node types, a new graph pass, CFG lowering, and StackIR/MSIL patterns. Likely a multi-spec effort.

**Path B — Encode in `VariableNode` with expression edges**: Extend `VariableNode` to carry an optional `Expr` string (e.g. `"a+b"`) and wire `DependsOn` edges to the operand variables. `MsilGenerationPass` interprets the expression string. Faster but fragile — expression parsing in the emitter is ad-hoc.

This spec describes the desired end state. If `ArithmeticNode` and `AssignNode` do not exist, the acceptance criterion (binary produces correct output) will fail at the compilation stage, and the gap becomes its own spec. **Do not work around the missing types in the Markdown** — the spec should reflect the intended program so the failure is a clear signal.

A prerequisite spec for `ArithmeticNode` + `AssignNode` + their CFG/StackIR/MSIL lowering should be written and implemented before attempting to run this example through the pipeline.

## Acceptance Criteria

1. `scripts/run.sh examples/Fibonacci/Fibonacci.md` completes without error. ✓
2. The compiled binary prints exactly the 10 lines in the `## Expected Output` block. ✓
3. `AcceptanceVerificationPass` reports 10/10 assertions passing. ✓
4. All existing example programs still pass. ✓
5. All unit tests pass (278/278). ✓

## Not In Scope

- Recursive Fibonacci — the pipeline only handles iterative programs with explicit loop bounds.
- Memoization or dynamic programming representations.
- Any change to the `.spec` text format.
