# Spec 33 — Multiple Inputs Example (Calculator)

**Status:** Incomplete
**Depends on:** Spec 32 (Guesser — int input, comparison branching)

## What this example is

`examples/Calculator/Calculator.md` describes a program that:

1. Prompts for and reads a first integer.
2. Prompts for and reads a second integer.
3. Prints their sum (or product, or difference — one operation, chosen at spec time).

## Why this matters

All current examples have at most one runtime input. This example tests whether the linear CFG lowering, InputNode ordering, and string-local tracking extend naturally to multiple reads in sequence. It also validates that the graph repository can store and retrieve programs with more than one InputNode.

## New constructs required

No new node types are expected — this should compose cleanly from `InputNode` (×2) and `AssignNode` (arithmetic on input values). The main question is whether `CfgPass.LowerLinear` handles multiple `InputNode`s in sequence correctly, and whether `StackIrPass` tracks two string-to-int parse calls without collision.

If `type: int, source: stdin` was introduced in Spec 32, this example exercises it twice.

## Spec format

No new keywords. Two `variable:` blocks with `source: stdin`, one `assign:` block, one `print:` block.

## LLM extraction surface

Incremental stress test: the model must emit two `variable:` blocks with `source: stdin` in the correct order, plus an `assign:` that references both inputs. This is the first example where the model must track two named inputs simultaneously. If it conflates them or drops one, the extraction cliff is closer than expected.

## Acceptance

```
## Test Input
3
7
## Expected Output
10
```

Two lines of test input. `AcceptanceVerificationPass` currently pipes a single `TestInput` line — this example requires writing two lines to stdin before the process reads them. `CompilationContext.TestInput` may need to become `string[]` (or newline-joined) to support multi-line input.

## Scope boundary

Two integer inputs, one arithmetic operation, no branching. Dynamic operation selection (reading the operator from stdin) is out of scope.
