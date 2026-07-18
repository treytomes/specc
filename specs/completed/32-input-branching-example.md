# Spec 32 — Input Branching Example (Guesser)

**Status:** Incomplete
**Depends on:** Spec 29 (Greetings — string I/O, linear programs) ✓
**Blocks:** Spec 33 (Calculator), Spec 34 (Collatz)

## Blockers to implement before this spec

- `type: int, source: stdin` in `SemanticGraphPass` — emit `InputNode` with integer type, lower to `ReadLine` + `int.Parse` + `StlocS`
- `compare: lt | gt | eq` and `value:` fields on `branch:` in `SemanticGraphPass`
- `CfgPass` pattern for comparison-based branches (no `ModuloNode`, uses `ComparisonNode` instead)
- `StackIrPass` patterns for `clt`, `cgt`, `ceq` against a fixed integer value
- `AcceptanceVerificationPass` — `TestInput` already pipes one line; int input reuses this path

## What this example is

`examples/Guesser/Guesser.md` describes a program that:

1. Reads an integer from stdin.
2. Branches on its value (e.g. prints "Low", "High", or "Correct" based on comparison to a fixed target).

This is the first example where control flow depends on runtime input, not on a loop counter or array index.

## Why this matters

Every current example branches on a fixed divisor or a fixed iteration count — the branch condition is computable at compile time. `Guesser` branches on a value that doesn't exist until the program runs. This stress-tests whether the spec format and graph representation are genuinely general or are tuned to the loop-divisor pattern.

## New constructs required

### `int` input variable (`source: stdin`, `type: int`)

`SemanticGraphPass` currently creates an `InputNode` only for `type: string, source: stdin`. For `type: int`, it should emit a `VariableNode` with a `ReadInt` flag (or a new `IntInputNode`) plus the appropriate StackIR lowering: `Console.ReadLine()` → `int.Parse()` → `StlocS`.

Alternative: keep `InputNode` type-agnostic; add a `Type` property (`"int"` or `"string"`); lower to `ReadLine + int.Parse` or `ReadLine` accordingly.

### Branching on a variable value

Current `BranchNode` / `ModuloNode` pattern assumes the condition is `n % divisor == 0`. A comparison-based branch (e.g. `n < 50`, `n == 42`) requires a `ComparisonNode` connected directly to the `BranchNode` via `DependsOn` without a `ModuloNode`.

`CfgPass` must detect this pattern and emit `ldloc n / ldc.i4 target / clt or ceq / brfalse` instead of the modulo check sequence.

## Spec format additions

```
variable:
  name: guess
  type: int
  source: stdin

branch:
  condition: too_low
  compare: lt
  value: 42
  true_output: "Too low."

branch:
  condition: too_high
  compare: gt
  value: 42
  true_output: "Too high."

branch:
  condition: correct
  true_output: "Correct!"
```

`compare: lt | gt | eq` on a `branch:` block signals a value comparison rather than a modulo check.

## LLM extraction surface

This is the first new spec format addition since Greetings. The system prompt will grow by ~10 lines. The critical test: does ministral-3b reliably use `compare:` + `value:` instead of inventing `divisor:` for this pattern? If not, it signals that the extraction cliff is shallower than expected — and Spec 21 (direct graph extraction) moves up in priority.

## Acceptance

```
## Test Input
42
## Expected Output
Correct!
```

Additional test runs (not in the Markdown file) with inputs 10 and 99 should produce "Too low." and "Too high." respectively. These can be manually verified.

## Scope boundary

Single integer input, three fixed branches, no loop. General comparison expressions (e.g. `n >= 42`, chained conditions) are out of scope.
