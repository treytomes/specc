# IronLlm — CLAUDE.md

## What this project is

IronLlm is a compiler that treats programs as semantic objects rather than text. It takes a `.spec` or Markdown description of a program and lowers it through a series of deterministic passes to a native executable. An LLM-powered embedding pass attaches learned vectors to each graph node — meaning as metadata, not as a driver of control flow.

This is not an agentic coding platform. The LLM does not author code, drive decisions, or orchestrate the pipeline. It does two specific things: extract a structured spec from Markdown prose (ministral-3b), and produce per-node embeddings that make graph nodes semantically comparable (mxbai-embed-large). Everything else — CFG construction, stack lowering, IL emission, acceptance verification — is deterministic code.

The experiment this is pointing toward: if programs are graphs with semantic embeddings, they become queryable and comparable across compilations. Two programs that implement the same intent should be close in embedding space. Graph structure could be refined by gradient (Spec 04). Prior compilations could inform future ones (Spec 03). This is the "differentiable semantic graph" idea — program structure as something that can be searched, interpolated, and refined, not just parsed and executed.

## Project layout

```
IronLlm/
  Graph/          Node, Edge, CfgBlock, StackInstruction type definitions
  Passes/         One file per compiler pass + CompilationContext + ArtifactWriter
  Program.cs      Incremental pipeline runner (System.CommandLine + DI)
IronLlm.Tests/
  Passes/         Per-pass unit tests
  Fixtures/       PipelineFixtures, FakeLogger
examples/
  FizzBuzz/       FizzBuzz.md + artifacts/ (generated, gitignored)
scripts/
  install.sh      Dependency check and setup
  build.sh        dotnet build wrapper
  run.sh          Run the compiler (defaults to FizzBuzz example)
  test.sh         Build + unit tests + pipeline smoke test
  assemble.sh     Optional ilasm step for 06-program.il
specs/
  completed/      Implemented design specs
  incomplete/     Upcoming work
```

## Compiler passes

Each pass implements `ICompilerPass` (`Name`, `ArtifactFile`, `ExecuteAsync`, `LoadFromArtifactAsync`). The pipeline is **incremental**: if a pass's artifact file already exists in the output directory, the pass is skipped and context is loaded from disk. Deleting an artifact forces that pass (and all downstream passes, since they depend on context) to re-run.

| # | Pass | Artifact | How |
|---|------|----------|-----|
| 00 | `MarkdownSpecPass` | `00-extracted.spec` + `00-authorial-criteria.json` | LLM extracts `.spec` from Markdown; second call extracts acceptance rules from prose (`.md` input only) |
| 01 | `ParseSpecPass` | `01-spec.json` | Read `.spec` file into `RawSpec` |
| 02 | `SemanticGraphPass` | `02-semantic-graph.json` | Deterministic parser → typed node/edge graph |
| 02b | `GraphVisualizationPass` | `02b-semantic-graph.mmd` + `02c-semantic-graph.svg` | Mermaid flowchart and layered SVG from graph; `AssertionNode`s excluded |
| 02c | `AcceptanceCriteriaPass` | `00-acceptance.json` | Derives expected output deterministically from graph; adds `AssertionNode`s with `EdgeType.Asserts` edges |
| 03 | `EmbeddingPass` | `03-embeddings.json` | mxbai-embed-large via Ollama — one call per non-`AssertionNode`, non-`ArithmeticNode`, non-`AssignNode` |
| 03b | `SemanticNormalizationPass` | `03b-normalized-graph.json` | Cosine similarity against reference corpus; reclassifies and relabels nodes below threshold 0.60. `ArithmeticNode`/`AssignNode` are exact-typed from the parsed spec and skip this gate. |
| 03c | `RepositoryRetrievalPass` | (no artifact) | Retrieves similar prior compilations from the graph repository; populates `context.PriorCompilations` |
| 03a | `NodeMlpPass` | `03a-refined-embeddings.json` | Per-kind MLP refines raw embeddings; runs after normalization and retrieval so those passes see the unmodified Ollama vector space |
| 04 | `CfgPass` | `04-cfg.json` | Deterministic structural lowering; dispatches on graph shape (flat loop / bubble sort / selection sort) |
| 05 | `StackIrPass` | `05-stackir.json` | Pattern-match CFG instructions → stack opcodes |
| 06 | `MsilGenerationPass` | `06-program.il` | Stack IR → IL assembly text |
| 07 | `AssemblyEmitPass` | `07-program.dll` + `{Name}` | `PersistedAssemblyBuilder` → patched apphost launcher |
| 08 | `SemanticValidationPass` | (no artifact, runs before assembly) | Invariant checks on the semantic graph |
| 08 | `AcceptanceVerificationPass` | (terminal, no artifact) | Launch compiled binary; diff stdout against `AuthorialAssertions` (authorial intent) or `Assertions` (graph-derived) |
| 09 | `RepositoryPersistPass` | (no artifact) | Stores the completed compilation unit in the graph repository |

### `AssertionNode` and the acceptance circuit

`AcceptanceCriteriaPass` adds `AssertionNode`s to the semantic graph (connected to the program node via `EdgeType.Asserts`) and populates `context.Assertions`. These nodes are excluded from `EmbeddingPass` and `SemanticNormalizationPass` — they are metadata, not semantic program structure.

`AcceptanceVerificationPass` prefers `context.AuthorialAssertions` when non-empty. These come from a second LLM call in `MarkdownSpecPass` that extracts acceptance rules directly from the prose, breaking the circular dependency: graph-derived criteria can only verify internal consistency, not authorial intent.

For `.md` files that contain an `## Expected Output` fenced block, `MarkdownSpecPass` parses the block directly (no LLM call) and uses those lines as authorial assertions.

### Node types excluded from normalization

`AssertionNode`, `ArithmeticNode`, and `AssignNode` are all skipped by `SemanticNormalizationPass`. The first is metadata; the latter two are exact-typed from the parsed `.spec` and do not benefit from similarity-based validation against the reference corpus.

## The `.spec` file format

```
program: <Name>

loop:
  from: <int>
  to: <int>

branch:
  condition: <snake_case>
  divisor: <int>              # omit for the default branch
  compare: lt | gt | eq       # runtime comparison (alternative to divisor:)
  value: <int>                # integer rhs when using compare:
  compare_with: {variable}    # variable rhs for var-vs-var comparison
  true_output: "<string>"     # quoted string, or {variable}

variable:
  name: <identifier>
  type: <type>
  initial_value: <int>        # optional; sets the variable before the loop
  source: stdin               # optional; read from stdin at runtime

assign:
  target: <identifier>
  op: mul | add | sub | div | copy
  left: {variable} | <int>
  right: {variable} | <int>   # omit when op is copy

random:
  name: <identifier>
  min: <int>
  max: <int>                  # inclusive; generates Random.Shared.Next(min, max+1)

while:
  variable: <identifier>      # simple var-vs-int form
  condition: ne | eq | lt | gt
  value: <int>

while:                        # var-vs-var form (interactive loop)
  compare_lhs: {variable}
  compare: ne | eq | lt | gt
  compare_rhs: {variable} | <int>

# Inside a while: body, branch: blocks may use true_assign: instead of true_output:
branch:
  condition: <snake_case>
  divisor: <int>              # optional; modulo check
  compare_with: {variable}    # var-vs-var comparison inside while body
  true_assign:                # one or more; assign body executed when condition is true
    target: <identifier>
    op: mul | add | sub | div | copy
    left: {variable} | <int>
    right: {variable} | <int>
```

Branches are evaluated in declaration order. The `default` branch (no `divisor`) provides the fallback output. `assign:` blocks express arithmetic and variable copies. When `assign:` blocks are present, all `branch:`/`divisor:` entries are treated as LLM noise and dropped by `CfgPass`. For comparison-based branching (no loop), use `compare:` + `value:` instead of `divisor:` — `CfgPass` dispatches to `LowerComparisonBranch`. For var-vs-var comparison, use `compare_with: {variable}` instead of `value:`. The var-vs-var `while:` form (with `compare_lhs:`/`compare_rhs:`) lowers to an interactive do-while CFG. Alternatively, pass a `.md` file and `MarkdownSpecPass` will extract the spec from prose.

## Running

```bash
scripts/install.sh                    # verify/install dependencies
scripts/run.sh                        # compile examples/FizzBuzz/FizzBuzz.md
scripts/run.sh path/to/spec           # compile a different spec or .md
scripts/run.sh --spec path --force 03 # delete and re-run from EmbeddingPass
scripts/test.sh                       # build + unit tests + pipeline smoke test
```

The compiled executable is written to `<artifacts-dir>/<ProgramName>` and is directly runnable.

## Dependencies

- .NET 10 SDK
- Ollama running on `http://localhost:11434`
- Models: `mxbai-embed-large:latest` (embeddings), `ministral-3:3b` (Markdown spec ingestion)
- python3 (used by `test.sh` for JSON artifact inspection only)

`scripts/install.sh` checks all of these and pulls missing Ollama models automatically. Override endpoints and model names via `.env` or environment variables (`IRONLLM_OLLAMA_BASE`, `IRONLLM_EMBED_MODEL`, `IRONLLM_CHAT_MODEL`).

## Key design decisions

**LLM as one optional pass, not the orchestrator.** Embeddings are metadata on the graph — they don't change structure. CFG is built deterministically. The LLM is reserved for tasks that genuinely require semantic understanding: extracting a spec from prose, and producing comparable vector representations of graph nodes.

**Incremental by artifact, not by timestamp.** Each pass owns a filename. If the file exists, the pass is skipped. Deleting `04-cfg.json` re-lowers the graph without re-embedding. The expensive embedding pass only runs once per spec.

**Authorial intent over graph-derived criteria.** `MarkdownSpecPass` makes two LLM calls: one to extract the spec, one to extract acceptance rules from the same prose. The verification pass uses the prose-derived rules when available so bugs introduced by `MarkdownSpecPass` or `SemanticGraphPass` themselves can be caught.

**apphost patching in-process.** `AssemblyEmitPass` uses `PersistedAssemblyBuilder` for PE emission and patches the SDK's `apphost` binary to produce a directly executable launcher — no `ilasm`, no `dotnet publish`, no shell-out.

## Completed examples

| Example | Description | Status |
|---------|-------------|--------|
| FizzBuzz | Classic 1–100 divisibility label program | 100/100 assertions pass |
| Fizz | Single-branch variant (divisible by 3 → "Fizz") | Pass |
| FizzBuzzHundred | Two-branch variant (÷7 "Fizz", ÷11 "Buzz") | Pass |
| CountDown | Print 1–10, no branches | Pass |
| BubbleSort | 10-element in-place sort, array lowering | 10/10 assertions pass |
| SelectionSort | 8-element selection sort | 8/8 assertions pass |
| Multiples | Loop 1–12, print n×7 each iteration | 12/12 assertions pass |
| Fibonacci | Print first 10 Fibonacci numbers via a, b, tmp variables | 10/10 assertions pass |
| Guesser | Read int from stdin, compare to 42, print hint | Pass (3/3 test cases) |
| Calculator | Read two ints from stdin, print their sum | Pass (3/3 test cases) |
| Collatz | Read int from stdin, run Collatz sequence, while loop + div/mul/add | Pass (3/3 test cases) |
| GuessingGame | Random target 1–100, stdin guesses, while loop + var-vs-var comparison | Pass |

## Upcoming work

See `specs/incomplete/` for detailed design docs:

| Spec | Title | Notes |
|------|-------|-------|
| 41 | Node MLP training loop | Offline contrastive + type-classification loss over `Contains`/`DependsOn` edges only; CFG back-edges excluded to avoid recurrent gradient paths |
| 42 | User-defined intrinsics | `intrinsics.json` alongside `.md` loads third-party .NET assemblies; `call:` spec construct emits named intrinsics directly; unblocks OpenTK, Terminal.Gui etc. |
| 20 | Roadmap to self-hosting | Long-horizon exploration |
| 21 | Direct graph extraction | Skip MarkdownSpecPass for well-formed inputs — deferred, needs ≥7B model |
| 29 | Greetings example | `InputNode`, string variables, linear CFG, stdin acceptance testing |
| 35 | Embedding geometry validation | `scripts/geometry.py` — 2/3 cluster checks pass; BubbleSort↔SelectionSort narrowly fails vs BubbleSort↔FizzBuzz (0.928 vs 0.932) |

