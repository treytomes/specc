# Spec 13 — Logging and DI Inside Passes

**Status:** Ready to implement  
**Scope:** `ICompilerPass`, all pass classes, `IronLlm.Tests` fixture updates

## Problem

After Spec 09, `CompilationPipeline` logs pass start/skip/complete at `Information` and artifact writes at `Debug`. But everything *inside* a pass is invisible: the 11-block CFG is constructed silently, every node embedding triggers a network call with no trace, and a 3-second Ollama round-trip is indistinguishable from a 30-second one. When `--verbose` is set, the `Debug` threshold opens up but there is nothing at that level to show.

There is also a structural problem: passes have no way to access a logger because `ICompilerPass` has no injection point. Every pass that wants structured output currently has to reach for `Console.Write` (which several did before Spec 09 cleaned them up) or stay silent. The DI container built in Spec 09 is wired up but only delivers `ILogger` as far as `CompilationPipeline`.

## Goal

Passes log their own work. `--verbose` reveals the full picture: node counts, timing, per-instruction lowering decisions, embedding call results. Passes receive their dependencies — logger and any future services — via constructor injection, consistent with the pattern already established by `EmbeddingPass`.

## Changes

### 1. Add `ILogger` injection point to `ICompilerPass`

The interface does not change — adding a required logger to the interface would force all implementors to take a constructor argument and would break test construction via `new ParseSpecPass()`. Instead, the convention is: **passes that have meaningful internal events accept `ILogger<T>` in their constructor.** Passes with nothing to say stay zero-argument.

The DI registration in `Program.cs` already handles this automatically: `AddTransient<ICompilerPass, ParseSpecPass>()` will inject `ILogger<ParseSpecPass>` if the constructor requests it.

Test construction via `new SomePass()` breaks only for passes that add the logger argument. The fixture helpers in `IronLlm.Tests` use `Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance` for those cases.

### 2. Per-pass logging plan

#### `ParseSpecPass`
- `LogDebug`: spec file path, character count of raw spec

#### `SemanticGraphPass`
- `LogDebug`: node count and edge count after graph construction
- `LogDebug`: each node kind encountered (one line per distinct kind, e.g. `"Node kinds: ProgramNode×1, LoopNode×1, BranchNode×4, …"`)
- `LogWarning`: if any spec section (loop, variable) is absent — the graph was built but may be incomplete

#### `EmbeddingPass` *(already has constructor injection — add logger)*
- `LogInformation`: total node count being embedded
- `LogDebug`: each node label + resulting vector dimension as it completes
- `LogDebug`: total elapsed time across all embedding calls

#### `CfgPass`
- `LogDebug`: each block label emitted, with its successor(s)
- `LogDebug`: total block count
- `LogWarning`: if validation detects a block with no successors other than `exit` (may indicate a dead branch)

#### `StackIrPass`
- `LogDebug`: each CFG block being lowered (block label + instruction count)
- `LogDebug`: total instruction count in the output IR
- `LogWarning`: if `LowerInstruction` produces no ops for a non-empty CFG instruction string (unrecognised pattern — currently silent data loss)

#### `MsilGenerationPass`
- `LogDebug`: total line count of the generated IL
- `LogDebug`: method name and entry point declaration

#### `AssemblyEmitPass`
- `LogInformation`: apphost path selected and its score (from `FindAppHost`)
- `LogDebug`: PE blob size in bytes
- `LogDebug`: runtimeconfig.json path written

### 3. Timing

Each pass that logs at `Information` should also log elapsed time so slow Ollama calls are surfaced without `--verbose`:

```csharp
var sw = Stopwatch.StartNew();
// ... do work ...
_logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
```

`CompilationPipeline` already owns the outer timing; per-pass timing belongs inside the pass so the cost is attributed correctly.

### 4. Unrecognised pattern warning in `StackIrPass`

Currently, `LowerInstruction` silently yields nothing for strings it does not recognise. After this spec, it emits:

```csharp
_logger.LogWarning(
    "StackIR: unrecognised instruction pattern in block {Block}: \"{Instruction}\"",
    currentBlockLabel, instr);
```

This is the most operationally important log event in the pipeline: silent drops in the IR mean wrong output at runtime with no indication of why.

### 5. `NullLogger` in tests

Test files that construct passes directly must pass a `NullLogger`. The fixture helper handles this:

```csharp
// PipelineFixtures.cs additions
public static CfgPass MakeCfgPass() =>
    new CfgPass(NullLogger<CfgPass>.Instance);

public static StackIrPass MakeStackIrPass() =>
    new StackIrPass(NullLogger<StackIrPass>.Instance);

// etc. for each pass that gains a logger argument
```

`AfterCfg()`, `AfterStackIr()`, etc. use these factory helpers instead of `new CfgPass()` directly.

## What does NOT change

- `ICompilerPass` interface signature — no `ILogger` in the contract
- Passes that have nothing meaningful to log stay zero-argument
- `ArtifactWriter` already logs via the passed-in logger; no change needed
- `CompilationPipeline` pass-level logging stays as-is; pass-internal logging is additive

## Log level guide (for this codebase)

| Level | Use |
|-------|-----|
| `Error` | Unrecoverable failure (throw instead if recoverable) |
| `Warning` | Data loss risk or degraded output (unrecognised IR pattern, missing graph section) |
| `Information` | One line per pass: start (from pipeline), completion with elapsed time |
| `Debug` | Per-item detail: node kinds, block labels, instruction counts, file paths |
| `Trace` | Not used — `Debug` is the floor |

## Test coverage

The `LogWarning` cases are the most important to test:

- `StackIrPass` emits a warning when given a CFG block whose instruction string matches no pattern
- `SemanticGraphPass` emits a warning when the spec has no `loop:` block
- `CfgPass` validation already throws on broken successors; the new warning is for structurally valid but semantically suspicious CFGs

Use `Microsoft.Extensions.Logging.Testing` (in-box in .NET 8+) or a simple `ILogger` spy to assert warning emission:

```csharp
var logger = new FakeLogger<StackIrPass>();
var pass   = new StackIrPass(logger);
// ... run pass ...
Assert.Contains(logger.Collector.GetSnapshot(),
    e => e.Level == LogLevel.Warning && e.Message.Contains("unrecognised"));
```

## Commit scope

One commit: `feat(passes): add structured logging and timing to all passes`

Touching: all pass files, `PipelineFixtures.cs`, test files for warning assertions.
