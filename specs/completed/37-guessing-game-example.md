# Spec 37 — Guessing Game Example

**Status:** Not started
**Depends on:** Spec 32 (Guesser — int input, comparison branching) ✓
**Blocks:** nothing (terminal stress-test example)

## What this example is

`examples/GuessingGame/GuessingGame.md` describes a program that:

1. Picks a random integer between 1 and 100.
2. Prompts the user to guess.
3. Reads an integer guess from stdin.
4. Prints "Too low!" if the guess is less than the target.
5. Prints "Too high!" if the guess is greater than the target.
6. Prints "Correct!" and exits if the guess equals the target.
7. Returns to step 2 for any incorrect guess.

This is the first example that requires all three blockers below simultaneously. It does not cleanly decompose: the `while:` loop, `var op var` comparisons, and `random:` construct are all load-bearing.

## Blockers

### 1. `random:` construct

The language has no way to generate a random number. The simplest addition is a `random:` block that declares a variable whose initial value is a runtime random integer:

```
random:
  name: target
  min: 1
  max: 100
```

This maps to a new `RandomNode(name, min, max)` in the graph. In StackIR, it lowers to a new `RandInt` opcode that calls `Random.Shared.Next(min, max + 1)` and stores the result in the named local.

**New graph node:**
```csharp
public record RandomNode(Guid Id, string Label, string Name, int Min, int Max) : Node(Id, Label);
```

**New StackIR opcode:**
```csharp
RandInt, // call Random.Shared.Next(int, int); operand = "min:max"; pushes int result
```

**IL emit (MsilGenerationPass / AssemblyEmitPass):**
```il
ldc.i4 {min}
ldc.i4 {max+1}
call int32 [mscorlib]System.Random::get_Shared()  // not quite right — see below
callvirt int32 [mscorlib]System.Random::Next(int32, int32)
```

Actually: `Random.Shared` is a static property returning a `Random` instance. The IL is:
```il
call class [mscorlib]System.Random [mscorlib]System.Random::get_Shared()
ldc.i4 {min}
ldc.i4 {max+1}
callvirt instance int32 [mscorlib]System.Random::Next(int32, int32)
stloc target
```

`AssemblyEmitPass` emits this via reflection: `typeof(Random).GetProperty("Shared").GetGetMethod()` + `typeof(Random).GetMethod("Next", [typeof(int), typeof(int)])`.

**Spec format:**
```
random:
  name: <identifier>
  min: <int>
  max: <int>
```

`SemanticGraphPass` parses this block and emits a `RandomNode`. `CfgPass` emits `rand_int {name} {min} {max}` in the entry block's initialization section. `StackIrPass` pattern: `^rand_int\s+(\w+)\s+(\d+)\s+(\d+)$` → `RandInt` opcode.

---

### 2. `var op var` comparison

`ComparisonNode` currently holds a fixed integer `Value`. `guess < target` requires both sides to be variable references. Extend `ComparisonNode` to support a variable right-hand side:

```csharp
public record ComparisonNode(Guid Id, string Label, string Op,
    int Value = 0, string? RhsVar = null) : Node(Id, Label);
```

When `RhsVar` is set, `Value` is ignored. The spec format gains a `compare_with:` field as an alternative to `value:`:

```
branch:
  condition: too_low
  compare: lt
  compare_with: {target}    # variable rhs — alternative to value: <int>
  true_output: "Too low!"
```

`SemanticGraphPass` parses `compare_with:`, strips braces, and sets `RhsVar` on the emitted `ComparisonNode`.

`CfgPass.LowerComparisonBranch` emits a new CFG instruction pattern when `RhsVar` is set:
```
if guess lt {target}        # var op var variant
```

`StackIrPass` gains a new pattern: `^if\s+(\w+)\s+(lt|gt|eq|ne)\s+\{(\w+)\}$` → loads both locals, emits `Clt`/`Cgt`/`Ceq`. For `ne`, emit `Ceq` + `ldc.i4 0` + `Ceq` (double-negate: `eq 0` means not-equal).

---

### 3. `while:` loop

The game requires a loop that runs until the guess equals the target. The `while:` loop is structurally different from the counted `loop:` — it has no fixed bounds and exits on a condition.

```
while:
  condition: not_correct
  compare_lhs: {guess}
  compare: ne
  compare_rhs: {target}
```

This maps to a new `WhileLoopNode`:

```csharp
public record WhileLoopNode(Guid Id, string Label,
    string Condition, string LhsVar, string Op, string RhsVar) : Node(Id, Label);
```

The loop body is the ordered set of nodes connected to the `WhileLoopNode` via `Contains` edges — same pattern as `LoopNode`.

`CfgPass` gains a `LowerWhileLoop` path:

```
entry:      [initialization — random, prints before the loop]
while_test: if guess ne target → loop_body / exit
loop_body:  read guess
            [comparison branches in declaration order]
            → while_test
exit:
```

The comparison branches inside the loop body use the `var op var` form from blocker 2.

**Spec format:**
```
while:
  condition: <snake_case>   # label for the loop node
  compare_lhs: {variable}
  compare: ne | eq | lt | gt
  compare_rhs: {variable} | <int>
```

`SemanticGraphPass` parses this block and emits a `WhileLoopNode`. The body nodes (reads, prints, branches) that follow in the spec are connected as `Contains` children of the `WhileLoopNode`.

**CFG shape:**

The key structural decision is whether the while condition is checked before or after the first body execution. For GuessingGame, we want "do-while" semantics: always read at least one guess. Two options:

1. `do-while`: jump directly into `loop_body` first, then check at the bottom.
2. Initialize the loop variable to a sentinel before the loop, check first.

Option 1 is simpler to lower for this case. The first block in the CFG is the body; the while test is at the bottom of the body.

```
entry:          target = rand_int 1 100
                print "..."
                → read_guess

read_guess:     read guess
                → check_lt

check_lt:       if guess lt {target}    → print_too_low / check_gt
print_too_low:  print "Too low!"        → read_guess
check_gt:       if guess gt {target}    → print_too_high / print_correct
print_too_high: print "Too high!"       → read_guess
print_correct:  print "Correct!"        → exit
exit:
```

Note: the "correct" branch is detected by falling through both lt and gt checks (neither true), so no explicit `eq` test is needed. The default path after both checks fail is the print-correct block.

---

## Acceptance

The program is interactive — no `## Expected Output` block, no `## Test Input`. Acceptance is manual: run the binary, provide guesses, verify the hint messages and final "Correct!" response.

For automated acceptance in future: extend `AcceptanceVerificationPass` to support multi-line `TestInput` (one line per interaction turn) and a scripted expected-output sequence. This is Spec 33's multi-line input problem applied to an interactive loop.

## Spec format additions summary

```
random:
  name: <identifier>
  min: <int>
  max: <int>

while:
  condition: <snake_case>
  compare_lhs: {variable}
  compare: ne | eq | lt | gt
  compare_rhs: {variable} | <int>

branch:                       # existing — extended with compare_with:
  condition: <snake_case>
  compare: lt | gt | eq | ne
  compare_with: {variable}    # NEW: variable rhs instead of value: <int>
  true_output: "<string>"
```

## Implementation order

1. `random:` + `RandomNode` + `RandInt` opcode — standalone, no dependencies
2. `compare_with:` on `ComparisonNode` — extends Spec 32's `var op const` to `var op var`
3. `while:` + `WhileLoopNode` + `LowerWhileLoop` in `CfgPass` — depends on both above

Each can be implemented and tested independently before assembling the full GuessingGame example.

## LLM extraction surface

All three constructs are new. The critical question: can ministral-3b use `random:`, `while:`, and `compare_with:` correctly when the system prompt describes them? This is likely the extraction cliff — three new constructs added simultaneously is a significant prompt expansion. Spec 36 (construct router) would limit the extraction call to only the relevant families (`input`, `random`, `while`) and might keep the model within its reliable window.
