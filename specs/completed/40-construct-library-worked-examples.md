# Spec 40 — SpecConstructLibrary Worked Examples

**Status:** Not started
**Scope:** `SpecConstructLibrary.cs`

## Motivation

Each section in `SpecConstructLibrary` describes the syntax of a construct family, but gives the model no concrete template to follow. A model that has seen the `while:` syntax rules once may not produce a structurally correct spec; a model that has seen a complete minimal working example of a `while:` program is more likely to get the section structure right.

The direct-graph extraction spec (Spec 21) identified worked examples as the key fix for structural reliability: topology errors that persisted through prompt-only descriptions were corrected when concrete examples were added. The same principle applies here.

## Design

Add a `## Example` subsection to each construct section in `SpecConstructLibrary`. Each example is a complete minimal `.spec` that uses only that family's constructs — short enough to fit within the token budget for the section, concrete enough to demonstrate nesting and structure.

### Example programs per family

**`loop`** — CountDown (1–5, no branches):
```
program: CountDown
loop:
  from: 1
  to: 5
variable:
  name: n
  type: int
print: "{n}"
```

**`branch`** — Fizz (1–10, print Fizz if divisible by 3, else n):
```
program: Fizz
loop:
  from: 1
  to: 10
variable:
  name: n
  type: int
branch:
  condition: fizz
  divisor: 3
  true_output: "Fizz"
branch:
  condition: default
  true_output: "{n}"
```

**`arithmetic`** — Doubles (1–5, print n×2):
```
program: Doubles
loop:
  from: 1
  to: 5
variable:
  name: n
  type: int
variable:
  name: result
  type: int
assign:
  target: result
  op: mul
  left: {n}
  right: 2
print: "{result}"
```

**`input`** — Echo (read a number, print it):
```
program: Echo
variable:
  name: n
  type: int
  source: stdin
print: "{n}"
```

**`array`** — (omit full example; array programs are complex enough that a minimal example would be misleading. Describe the syntax with inline comments instead.)

**`while`** — Halve (keep halving until 1):
```
program: Halve
variable:
  name: n
  type: int
  source: stdin
while:
  variable: n
  condition: ne
  value: 1
print: "{n}"
branch:
  condition: default
  true_assign:
    target: n
    op: div
    left: {n}
    right: 2
```

## Implementation

Update each section string in `SpecConstructLibrary` to append the example after the syntax description, preceded by a `Example:` header line. The total token addition per section is ~30–50 tokens — well within budget.

## Acceptance criteria

1. A fresh Collatz extraction (cleared artifacts) with the `while` section containing the Halve example produces a spec that includes `while:` with correct `variable:`, `condition:`, and `value:` fields.
2. A FizzBuzz extraction with the `branch` section containing the Fizz example produces a spec with a `branch:` that includes `divisor:` and `true_output:`.
3. All existing tests pass.
