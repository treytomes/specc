# Spec 01 — Stack IR Lowering

**Status:** Ready to implement  
**Scope:** `StackIrPass.cs`

## Problem

`CfgPass` is fully deterministic — it derives block labels and instruction strings directly from the semantic graph (no LLM). The block structure it emits is:

- Entry block: initialize loop variable
- Loop-test block: compare variable against upper bound, branch to exit or body
- Body block: dispatch to modulo-check blocks
- Per-condition blocks: `check_{condition}` and `print_{condition}`
- Increment block: bump loop variable, jump back to loop-test
- Exit block

`StackIrPass` pattern-matches instruction strings to produce stack opcodes. Some of these patterns are incomplete or missing, so the loop-init, loop-test, and loop-increment blocks emit nothing — only the modulo-check and print blocks lower correctly. The generated MSIL is consequently incomplete.

## Instruction Contract

`CfgPass` now emits these deterministic instruction strings:

| Instruction string | Meaning |
|--------------------|---------|
| `n = {From}` | initialize loop variable (e.g. `n = 1`) |
| `if n > {To} goto exit` | loop termination test |
| `if n % {D} == 0` | divisibility check (one per modulo branch) |
| `print "{X}"` | output string literal |
| `print n` | output loop variable as integer |
| `n = n + 1` | loop increment |

The `{n}` placeholders are substituted with actual variable names and values from the graph.

## StackIrPass Changes

Extend `LowerInstruction` to handle all patterns. Each must produce a correct sequence of stack ops:

| Pattern | Stack ops |
|---------|-----------|
| `n = {From}` | `ldc.i4 From`, `stloc.0` |
| `if n > {To} goto exit` | `ldloc.0`, `ldc.i4 To`, `cgt`, `brfalse` (to next via block's SuccessorFalse) |
| `if n % {D} == 0` | `ldloc.0`, `ldc.i4 D`, `rem`, `ldc.i4 0`, `ceq` |
| `print "{X}"` | `ldstr "X"`, `call Console.WriteLine(string)` |
| `print n` | `ldloc.0`, `call Console.WriteLine(int32)` |
| `n = n + 1` | `ldloc.0`, `ldc.i4 1`, `add`, `stloc.0` |

## MsilGenerationPass Changes

Add the missing `cgt` opcode to the switch and to the `OpCode` enum:

```csharp
OpCode.Cgt => "    cgt",
```

## Acceptance Criterion

Running `scripts/run.sh` produces a `06-program.il` that, when assembled with `scripts/assemble.sh` and run, prints the correct FizzBuzz sequence 1–100. The pipeline's own `AcceptanceVerificationPass` already validates the native binary output; `ilasm` round-trip is the additional goal.
