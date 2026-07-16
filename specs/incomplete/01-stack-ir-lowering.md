# Spec 01 — Stack IR Lowering

**Status:** Ready to implement  
**Scope:** `StackIrPass.cs`, `CfgPass.cs` system prompt

## Problem

The CFG pass produces natural-language pseudo-instructions like:

```
"init n = 1"
"loop_start: check n <= 100"
"increment n: n = n + 1"
```

`StackIrPass` matches these with hand-written string patterns. The patterns miss the actual strings the LLM returns, so the entry, loop-test, and loop-increment blocks emit nothing — only the modulo-check blocks lower correctly. The generated MSIL is consequently incomplete.

## Root Cause

Two options exist; we should do both:

**Option A — Tighten the CFG prompt.** Mandate exact instruction strings in the system prompt and validate them in `CfgPass.Validate`. The LLM becomes narrowly constrained; the StackIrPass patterns become reliable.

**Option B — Strengthen StackIrPass patterns.** Match more loosely (contains `"n ="` and a digit, contains `"increment"`, contains `"check n"` and `"100"`). Less brittle if the LLM varies phrasing.

Both should be done. Prompts drift; patterns should be tolerant.

## Instruction Contract

Mandate these exact instruction strings in the CFG system prompt:

| Instruction string | Meaning |
|--------------------|---------|
| `n = 1` | initialize loop variable |
| `if n > 100 goto exit` | loop termination test |
| `if n % 15 == 0` | divisibility check |
| `if n % 3 == 0` | divisibility check |
| `if n % 5 == 0` | divisibility check |
| `print "FizzBuzz"` | output string |
| `print "Fizz"` | output string |
| `print "Buzz"` | output string |
| `print n` | output integer variable |
| `n = n + 1` | loop increment |

## StackIrPass Changes

Extend `LowerInstruction` to handle all ten patterns. Each must produce a correct sequence of stack ops:

- `n = 1` → `ldc.i4 1`, `stloc.0`
- `if n > 100 goto exit` → `ldloc.0`, `ldc.i4 100`, `cgt`, (brfalse handled by block's SuccessorFalse)
- `if n % D == 0` → `ldloc.0`, `ldc.i4 D`, `rem`, `ldc.i4 0`, `ceq`
- `print "X"` → `ldstr "X"`, `call Console.WriteLine(string)`
- `print n` → `ldloc.0`, `call Console.WriteLine(int32)`
- `n = n + 1` → `ldloc.0`, `ldc.i4 1`, `add`, `stloc.0`

## MsilGenerationPass Changes

Add the missing `cgt` opcode to the switch:

```csharp
OpCode.Cgt => $"    cgt",
```

Add `cgt` to the `OpCode` enum.

## Invariant to Add to CfgPass.Validate

After deserialization, assert that every non-exit block contains at least one instruction string that matches one of the ten known patterns. Reject with a descriptive error if not.

## Acceptance Criterion

Running `dotnet run` produces a `06-program.il` file that, when assembled with `ilasm FizzBuzz.il` and run, prints the correct FizzBuzz sequence 1–100.
