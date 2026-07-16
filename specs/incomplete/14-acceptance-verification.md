# Spec 14 — Acceptance Verification

**Status:** Ready to implement  
**Scope:** Two new passes (`AcceptanceCriteriaPass`, `AcceptanceVerificationPass`), one new graph type (`AssertionNode`), one `CompilationContext` field, one `CompilationException` subclass

## Problem

The compiled binary has no way to verify its own correctness. `test.sh` contains hand-written assertions that are disconnected from the program's specification — a spec change does not automatically update the acceptance criteria, and the criteria cannot travel with the program when it is distributed or re-compiled.

## Solution

Derive acceptance criteria deterministically from the semantic graph, then verify the compiled binary against them. The semantic graph already encodes every fact needed to enumerate the correct output: loop bounds, branch order, divisor values, print templates, and the loop variable.

The LLM is not involved in either pass. Criteria generation is a pure function of the graph. Verification is a subprocess call.

## Pipeline position

```
MarkdownSpecPass         ← 00-extracted.spec
ParseSpecPass            ← 01-spec.json
SemanticGraphPass        ← 02-semantic-graph.json
AcceptanceCriteriaPass   ← 00-acceptance.json   (NEW, runs after SemanticGraph)
EmbeddingPass            ← 03-embeddings.json
SemanticNormalizationPass← 03b-normalized-graph.json
CfgPass                  ← 04-cfg.json
StackIrPass              ← 05-stackir.json
MsilGenerationPass       ← 06-program.il
AssemblyEmitPass         ← 07-program.dll
AcceptanceVerificationPass  (NEW, terminal, no artifact)
```

`AcceptanceCriteriaPass` runs early — immediately after `SemanticGraphPass` — so the expected output is committed before any lowering pass can introduce bugs. It is registered unconditionally (not only for `.md` inputs) because criteria are derivable from any valid semantic graph.

`AcceptanceVerificationPass` is terminal and has no `ArtifactFile` (it produces no persistent output). It runs only when a launcher exists. If the graph contains no `AssertionNode`s, the pass is a no-op.

## `AcceptanceCriteriaPass`

### Algorithm

```
loop ← graph.Nodes.OfType<LoopNode>().Single()
branches ← modulo branches sorted by Divisor descending, then default branch
variable ← graph.Nodes.OfType<VariableNode>().Single()

for i in loop.From .. loop.To:
    matched ← false
    for branch in modulo_branches:
        if i % branch.Divisor == 0:
            assertions.Add(new AssertionNode(i, PrintFor(branch)))
            matched = true
            break
    if not matched:
        if default_branch exists:
            template ← PrintFor(default_branch)
            value ← template == "{variable}" ? i.ToString() : template
            assertions.Add(new AssertionNode(i, value))
        else:
            assertions.Add(new AssertionNode(i, i.ToString()))
```

`PrintFor(branch)` resolves the `PrintNode` connected to the branch via a `TrueBranch` edge, returning its `Template`. If the template equals `{n}` (or `{<variable name>}`), the substituted integer value is used.

### Artifact

`00-acceptance.json` — an ordered array of assertion objects:

```json
[
  { "iteration": 1,  "expected": "1"        },
  { "iteration": 2,  "expected": "2"        },
  { "iteration": 3,  "expected": "Fizz"     },
  { "iteration": 4,  "expected": "4"        },
  { "iteration": 5,  "expected": "Buzz"     },
  ...
  { "iteration": 15, "expected": "FizzBuzz" },
  ...
  { "iteration": 100,"expected": "Buzz"     }
]
```

The array is ordered by `iteration`. Total entries equals `loop.To - loop.From + 1`.

### Graph side-effect

For each assertion, add an `AssertionNode` to the semantic graph and connect it to the program node with a new `EdgeType.Asserts`. This makes the expected output a first-class part of the program's semantic representation.

```csharp
public record AssertionNode(Guid Id, string Label, int Iteration, string Expected)
    : Node(Id, Label);
```

```csharp
// in EdgeType enum:
Asserts,
```

`AssertionNode`s are not embedded (excluded from `EmbeddingPass`) and not lowered to CFG (excluded from `CfgPass`).

## `AcceptanceVerificationPass`

### Algorithm

```
assertions ← context.Assertions   // loaded from 00-acceptance.json
launcher   ← context.LauncherPath ?? dotnet context.AssemblyPath

if assertions is empty: return   // no-op

stdout ← Process.Start(launcher).StandardOutput.ReadToEnd()
lines  ← stdout.Split('\n', RemoveEmptyEntries)

if lines.Length != assertions.Count:
    throw AcceptanceFailureException(
        $"Expected {assertions.Count} output lines, got {lines.Length}")

failures ← []
for (i, assertion) in assertions.Enumerate():
    actual ← lines[i]
    if actual != assertion.Expected:
        failures.Add((assertion.Iteration, assertion.Expected, actual))

if failures.Any():
    throw AcceptanceFailureException(failures)
```

### `AcceptanceFailureException`

A subclass of `CompilationException` that carries a structured failure list:

```csharp
public class AcceptanceFailureException : CompilationException
{
    public IReadOnlyList<AcceptanceFailure> Failures { get; }
    // ...
}

public record AcceptanceFailure(int Iteration, string Expected, string Actual);
```

### Output

On success: `_logger.LogInformation("Acceptance: {Count}/{Total} assertions passed", ...)`

On failure: the exception message lists each failing line with its iteration number, expected value, and actual value.

## `CompilationContext` changes

```csharp
public List<AssertionRecord> Assertions { get; set; } = [];
```

```csharp
public record AssertionRecord(int Iteration, string Expected);
```

`AcceptanceCriteriaPass` populates this after generating the artifact. `AcceptanceVerificationPass` reads it.

## `ArtifactWriter` change

Add a case for `AcceptanceCriteriaPass` that serializes `context.Assertions` to `00-acceptance.json`.

## Relationship to `test.sh`

Once `AcceptanceVerificationPass` is in place, the output-correctness checks in `test.sh` are redundant — the pipeline self-verifies. They should be replaced with a single check that the pipeline exit code is 0.

## Failure modes

| Condition | Outcome |
|-----------|---------|
| Loop or modulo branch absent from graph | `InvalidOperationException` (graph precondition) |
| Binary exits non-zero | `AcceptanceFailureException` with process exit code |
| Binary produces wrong output | `AcceptanceFailureException` with per-line diff |
| Binary produces no output | `AcceptanceFailureException` ("0 lines, expected N") |

## Circular verification limitation

There is an inherent limitation in deriving acceptance criteria from the semantic graph: if `MarkdownSpecPass` or `SemanticGraphPass` misinterprets the prose, the graph will encode the wrong program — and `AcceptanceCriteriaPass` will derive wrong criteria from that wrong graph. The verification pass then checks the binary against those wrong criteria and passes. The bug is invisible because the error propagated uniformly through every layer.

In effect, graph-derived criteria verify internal consistency: "the binary matches what the graph says." They cannot verify authorial intent: "the binary matches what the author meant."

## Authorial intent extraction

When the Markdown document contains explicit acceptance criteria — stated in prose ("multiples of 3 should print Fizz"), as a rules table, or as concrete examples — those statements represent what the author actually intended. Extracting them independently of the `.spec` and graph breaks the circular dependency.

`MarkdownSpecPass` makes a second LLM call immediately after extracting the `.spec`. The call asks the model to extract the acceptance rules directly from the prose and return them as a compact JSON structure:

```json
{
  "loopFrom": 1,
  "loopTo": 100,
  "rules": [
    { "divisor": 15, "expected": "FizzBuzz" },
    { "divisor": 3,  "expected": "Fizz"    },
    { "divisor": 5,  "expected": "Buzz"    },
    { "isDefault": true, "expected": "{n}" }
  ]
}
```

`MarkdownSpecPass` then evaluates these rules locally (same algorithm as `AcceptanceCriteriaPass`) to produce an enumerated list of `AssertionRecord`s, stored in `context.AuthorialAssertions` and persisted as `00-authorial-criteria.json`.

If the prose does not contain extractable criteria (the model returns `{}`), `AuthorialAssertions` remains empty and behavior is unchanged.

`AcceptanceVerificationPass` uses `context.AuthorialAssertions` when non-empty, falling back to `context.Assertions` (graph-derived) otherwise. The log line indicates which source was used.

### `CompilationContext` addition

```csharp
public List<AssertionRecord> AuthorialAssertions { get; set; } = [];
```

### `MarkdownSpecPass` additions

- Private `ExtractAuthorialCriteriaAsync(string markdown)` — second LLM call returning `AuthorialCriteriaDto`
- Private `EvaluateRules(AuthorialCriteriaDto dto)` — local evaluation returning `List<AssertionRecord>`
- `ExecuteAsync` calls both extraction methods; writes `00-authorial-criteria.json` when rules are non-empty; populates `context.AuthorialAssertions`
- `LoadFromArtifactAsync` loads `00-authorial-criteria.json` from the same directory when present

### `AcceptanceVerificationPass` change

```csharp
var assertions = context.AuthorialAssertions.Count > 0
    ? context.AuthorialAssertions
    : context.Assertions;
_logger.LogInformation("Using {Source} assertions ({Count} total)",
    context.AuthorialAssertions.Count > 0 ? "authorial" : "graph-derived",
    assertions.Count);
```

## Test strategy

`AcceptanceCriteriaPass` is deterministic and has no external dependencies — test it directly:

- Given the FizzBuzz semantic graph, assert that 100 `AssertionRecord`s are produced.
- Assert that iteration 3 → "Fizz", 5 → "Buzz", 15 → "FizzBuzz", 1 → "1", 100 → "Buzz".
- Assert that the graph gains `AssertionNode`s after the pass runs.
- Assert that `LoadFromArtifactAsync` restores `context.Assertions` correctly.

`AcceptanceVerificationPass` is tested against the real compiled binary — no stubs. The
production pass calls `Process.Start(launcher)`, so the test does the same thing:

1. Use `PipelineFixtures.AfterStackIr()` to produce a context with stack IR.
2. Run `AssemblyEmitPass` to compile a real binary into a temp artifacts directory.
3. Run `AcceptanceCriteriaPass` against the same context to populate `context.Assertions`.
4. Run `AcceptanceVerificationPass` — it launches the binary and verifies its output.
5. Assert the pass completes without throwing.

The failure path (`AcceptanceFailureException`) is tested by mutating `context.Assertions`
after step 3 — replacing a correct expected value with a wrong one — then asserting the pass
throws with a failure list that names the corrupted iteration.

`MarkdownSpecPass` authorial criteria tests use a `StubChatClient` that returns queued responses — one for `.spec` extraction and one for criteria extraction. Tests verify:
- When the model returns valid rules, `context.AuthorialAssertions` is populated.
- When the model returns `{}`, `context.AuthorialAssertions` is empty.
- The criteria file is written to disk.
- `LoadFromArtifactAsync` restores `AuthorialAssertions` from `00-authorial-criteria.json`.

`AcceptanceVerificationPass` authorial preference is tested by populating both `Assertions` and `AuthorialAssertions` with different values and asserting the pass uses `AuthorialAssertions`.
