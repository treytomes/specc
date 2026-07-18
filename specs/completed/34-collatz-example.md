# Spec 34 — Collatz Example (Unbounded Loop)

**Status:** Incomplete
**Depends on:** Spec 32 (Guesser — int input, comparison branching); Spec 33 recommended but not strictly required
**Blocks:** Spec 35 (geometry validation needs program variety); decision gate for Spec 21 vs Spec 04

## Blockers to implement before this spec

- Everything in Spec 32 (int `InputNode`)
- `WhileLoopNode` (or generalized `LoopNode` with condition expression) in `Nodes.cs`
- `while:` spec keyword and parser in `SemanticGraphPass`
- `CfgPass` lowering for `WhileLoopNode`: `loop_top → test → brtrue exit → body → br loop_top`
- `assign: op: div` — integer division — in `SemanticGraphPass`, `StackIrPass` (`div` opcode), `MsilGenerationPass`, `AssemblyEmitPass`
- `AcceptanceCriteriaPass` must handle programs with a `WhileLoopNode` (cannot derive expected output statically — defer to authorial assertions only, same pattern as array programs)

## Extraction cliff note

This spec is designed to test and record where ministral-3b fails. If the model cannot reliably produce `while:` + nested conditional assigns + `op: div` from prose, document the exact failure mode. That failure is the trigger for prioritizing Spec 21.

## What this example is

`examples/Collatz/Collatz.md` describes the Collatz sequence starting from a given integer n:
- While n ≠ 1: if n is even, n = n / 2; else n = n * 3 + 1. Print each value.

This is the first example that requires an **unbounded loop** — a loop whose iteration count is not known at compile time.

## Why this matters

The current `LoopNode` has `From` and `To` fields: both are compile-time constants. Collatz terminates at a value (n == 1), not at a count. This is qualitatively different from FizzBuzz's `loop: from: 1 to: 100`. Implementing it forces a `while:` construct into the spec format and a `WhileLoopNode` (or a generalized `LoopNode` with a condition expression) into the graph.

This is the **Turing-completeness boundary**. If the compiler can lower an unbounded conditional loop to IL, it can express any iterative computation. Combined with integer arithmetic and input, it crosses from "useful subset" to "general-purpose."

## New constructs required

### `WhileLoopNode`

A loop parameterized by a condition expression rather than a fixed range.

```json
{ "kind": "WhileLoop", "label": "WhileLoop:n!=1", "variable": "n", "condition": "ne", "value": 1 }
```

`CfgPass` emits: `loop_top` → test condition → `brtrue exit` → body → `br loop_top`.

### Spec format

```
while:
  variable: n
  condition: ne
  value: 1
```

Or expressed as a termination condition (`condition: eq`, `value: 1` → exit when n == 1).

### Branch-on-parity

Collatz branches on even/odd — this is a modulo check (`n % 2 == 0`), which the current spec format already supports via `divisor: 2`. The body of the true branch assigns `n = n / 2` (new: integer division) and the false branch assigns `n = n * 3 + 1` (existing: mul + add).

Integer division (`div`) needs to be added as a new `assign: op: div` variant.

## LLM extraction cliff signal

This is the expected point where ministral-3b may fail to produce a valid spec. The combination of `while:` (new keyword), two nested `assign:` blocks inside branches, and integer division in a single prompt may exceed the model's reliable extraction window.

If extraction fails consistently, it confirms that Spec 21 (direct graph extraction) is prerequisite to Collatz, not just an optimization. Record the failure mode explicitly — it informs the prioritization decision between Spec 04 and Spec 21.

## Acceptance

```
## Test Input
6
## Expected Output
6
3
10
5
16
8
4
2
1
```

## Scope boundary

Single integer input, while loop, parity branching, integer division. Multiple simultaneous while conditions, nested while loops, and mutual recursion are out of scope.
