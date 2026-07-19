# Specc

A compiler that treats programs as semantic objects rather than text. You describe a program in Markdown; Specc extracts a typed semantic graph, attaches learned embeddings to each node, verifies the program against its own acceptance criteria, and lowers the graph to a native executable.

## The idea

Most "AI + code" tools use the LLM as author or orchestrator. Specc is something different: the LLM is one optional pass in a deterministic pipeline, and what it contributes is *meaning as metadata* — per-node vector embeddings that make graph nodes semantically comparable. The graph is not a transient parse tree; it is the program. It persists, carries its acceptance criteria as first-class nodes, and the semantic normalization pass catches cases where a node's meaning drifts from its structural role.

The deeper experiment: if programs are graphs with semantic embeddings, they become queryable and comparable across compilations. Two programs that implement the same intent should be close in embedding space. Graph structure could be refined by gradient (Spec 04). Prior compilations could inform future ones (Spec 03). This is the differentiable semantic graph — program structure as something that can be searched, interpolated, and refined, not just parsed and executed.

## Pipeline

Sixteen passes, each writing a named artifact to an output directory. The pipeline is **incremental**: if an artifact already exists, the pass is skipped and context is loaded from disk.

```
FizzBuzz.md
    │
    ▼ MarkdownSpecPass ──────────── 00-extracted.spec          LLM: extract spec from prose
    │                  └─────────── 00-authorial-criteria.json LLM or direct parse: acceptance rules
    ▼ ParseSpecPass ─────────────── 01-spec.json
    ▼ SemanticGraphPass ─────────── 02-semantic-graph.json     typed node/edge graph
    ▼ GraphVisualizationPass ─────── 02b-semantic-graph.mmd    Mermaid flowchart
    │                        └────── 02c-semantic-graph.svg    layered SVG (browser-ready)
    ▼ AcceptanceCriteriaPass ─────── 00-acceptance.json        expected output, derived from graph
    ▼ EmbeddingPass ─────────────── 03-embeddings.json         per-node vectors (mxbai-embed-large)
    ▼ RepositoryRetrievalPass ────── (no artifact)             retrieve similar prior compilations
    ▼ SemanticNormalizationPass ──── 03b-normalized-graph.json label normalization via cosine similarity
    ▼ NodeMlpPass ───────────────── 03a-refined-embeddings.json per-kind MLP refinement
    ▼ CfgPass ───────────────────── 04-cfg.json                control-flow graph
    ▼ CfgVisualizationPass ─────────04b-cfg-flowchart.mmd      CFG Mermaid flowchart
    │                        └────── 04c-cfg-flowchart.svg     CFG layered SVG
    ▼ StackIrPass ───────────────── 05-stackir.json            stack machine IR
    ▼ SemanticValidationPass ──────── (no artifact)            graph invariant checks
    ▼ MsilGenerationPass ────────── 06-program.il              IL assembly text
    ▼ AssemblyEmitPass ──────────── 07-program.dll             managed PE
    │                  └─────────── FizzBuzz                   native launcher
    ▼ AcceptanceVerificationPass    (no artifact)              launch binary, diff against criteria
    ▼ RepositoryPersistPass ──────── (no artifact)             persist compilation to graph repository
```

Two models participate in the pipeline. `MarkdownSpecPass` uses a small language model (ministral-3b) to extract a structured spec from prose. `EmbeddingPass` uses an embedding model (mxbai-embed-large) to attach a semantic vector to each graph node. Everything else is deterministic.

`AcceptanceVerificationPass` prefers acceptance rules extracted from the Markdown prose over rules derived from the graph. Graph-derived rules verify that the binary matches the graph — but if the extraction passes misinterpret the prose, both the graph and the criteria will be wrong and the check passes silently. Extracting criteria from the prose independently breaks this circular dependency. For `.md` files with an `## Expected Output` block, the expected lines are parsed directly without an LLM call.

## Quickstart

```bash
# 1. Install dependencies (checks .NET 10, Ollama, models; installs what's missing)
./scripts/install.sh

# 2. Compile the FizzBuzz example
./scripts/run.sh

# 3. Run the output
./examples/FizzBuzz/artifacts/FizzBuzz
```

Full test suite:

```bash
./scripts/test.sh
```

Force a pass to re-run by deleting its artifact or using `--force`:

```bash
# Re-run from the embedding pass onward
./scripts/run.sh --force 03-embeddings.json
```

## The spec format

Programs are described in a simple key-value DSL, or in plain Markdown (preferred). The Markdown path is more reliable — Specc injects verified prior compilations into the extraction prompt as structural templates, so the model pattern-matches against working examples rather than synthesizing from rules alone.

```
program: FizzBuzz

loop:
  from: 1
  to: 100

branch:
  condition: divisible_by_15
  divisor: 15
  true_output: "FizzBuzz"

branch:
  condition: divisible_by_3
  divisor: 3
  true_output: "Fizz"

branch:
  condition: divisible_by_5
  divisor: 5
  true_output: "Buzz"

branch:
  condition: default
  true_output: "{n}"

variable:
  name: n
  type: int
```

Branches are evaluated in declaration order. The `default` branch has no `divisor`.

For programs that compute values, use `assign:` blocks and `initial_value:`:

```
program: Fibonacci

loop:
  from: 1
  to: 10

variable:
  name: a
  type: int
  initial_value: 1

variable:
  name: b
  type: int
  initial_value: 0

variable:
  name: tmp
  type: int

assign:
  target: tmp
  op: copy
  left: {a}

assign:
  target: a
  op: add
  left: {a}
  right: {b}

assign:
  target: b
  op: copy
  left: {tmp}

branch:
  condition: default
  true_output: {a}
```

Valid ops: `mul`, `add`, `sub`, `div`, `copy`. When `assign:` blocks are present, any `branch:`/`divisor:` entries in the same spec are treated as noise and dropped. For interactive programs, use `source: stdin` on a variable and a `while:` block for the loop condition.

## Working examples

| Example | Description | Result |
|---------|-------------|--------|
| FizzBuzz | 1–100 divisibility labels | 100/100 |
| Fizz | Single-branch variant (÷3 → "Fizz") | Pass |
| FizzBuzzHundred | Two-branch variant (÷7 "Fizz", ÷11 "Buzz") | Pass |
| CountDown | Print 1–10, no branches | Pass |
| BubbleSort | 10-element in-place sort | 10/10 |
| SelectionSort | 8-element selection sort | 8/8 |
| Multiples | Print first 12 multiples of 7 | 12/12 |
| Fibonacci | Print first 10 Fibonacci numbers | 10/10 |
| Guesser | Read int from stdin, compare to 42, print hint | Pass (3/3) |
| Calculator | Read two ints from stdin, print their sum | Pass (3/3) |
| Collatz | Read int from stdin, run Collatz sequence | Pass (3/3) |
| GuessingGame | Random target 1–100, stdin guesses, var-vs-var while loop | Pass |
| DiceRoll | Roll a d6 and print the result | Pass |
| SimpleGuess | Interactive while loop with hardcoded target | Pass |

Run any example:

```bash
./scripts/run.sh examples/Fibonacci/Fibonacci.md
```

## Project structure

```
Specc/
  Graph/          Node.cs, Edge.cs, CfgBlock.cs, StackInstruction.cs
  Learning/       NodeMlp.cs, NodeMlpRegistry.cs
  Passes/         One file per pass + CompilationContext + ArtifactWriter
  Passes/Repository/  CompiledUnit.cs, GraphRepository.cs
  Program.cs      CLI entry point (System.CommandLine + DI + Serilog)
Specc.Tests/
  Passes/         Per-pass unit tests
  Fixtures/       PipelineFixtures, FakeLogger
examples/
  FizzBuzz/       FizzBuzz.md + artifacts/ (generated)
  ...             one directory per working example
scripts/
  install.sh      Dependency setup
  build.sh        dotnet build
  run.sh          Run the compiler
  test.sh         Build + unit tests + pipeline smoke test
  assemble.sh     Optional: re-assemble 06-program.il with ilasm
specs/
  completed/      Implemented design specs
  incomplete/     Upcoming work
```

## Prerequisites

| Dependency | Version | Purpose |
|------------|---------|---------|
| .NET SDK | 10.0+ | Build and run |
| Ollama | latest | LLM inference host |
| mxbai-embed-large | latest | Embedding pass |
| ministral-3:3b | latest | Markdown spec ingestion |

`scripts/install.sh` handles all of these. Override endpoints and model names via `.env` or environment variables (`SPECC_OLLAMA_BASE`, `SPECC_EMBED_MODEL`, `SPECC_CHAT_MODEL`).

## How the incremental pipeline works

Each `ICompilerPass` declares an `ArtifactFile`. Before executing a pass, the runner checks if that file exists in the artifacts directory. If it does, the pass is skipped and context is loaded from disk. This means:

- The expensive embedding pass (one Ollama call per graph node) only runs once per spec.
- Editing a downstream pass, deleting its artifact, and re-running re-executes only from that point.
- Deleting `04-cfg.json` through `07-program.dll` re-lowers the graph without re-embedding.

## Architecture notes

**Why a deterministic CFG?** An earlier design called an LLM to generate the control-flow graph from the semantic graph. The problem: constructing a correct CFG from a typed graph is a mechanical transformation, not a task that requires semantic understanding. Rewriting `CfgPass` as deterministic code eliminated an entire class of errors and made the pipeline testable without an Ollama instance.

**Why authorial intent for verification?** Acceptance criteria derived from the graph verify that the binary matches the graph — but if `MarkdownSpecPass` or `SemanticGraphPass` misinterprets the prose, the graph and the criteria will both be wrong and the check will pass silently. Extracting criteria from the prose independently breaks this circular dependency.

**Why apphost patching?** `AssemblyEmitPass` uses `PersistedAssemblyBuilder` (.NET 9+) to emit the managed PE entirely in-process, then copies the SDK's `apphost` binary and patches its filename placeholder to produce a directly runnable native launcher — no `dotnet publish`, no `ilasm`, no shell-out.

**Why a semantic graph at all?** It is the persistence layer for program intent. Once a program has been compiled and its graph and embeddings are saved, future compilations of similar programs can retrieve and reuse prior structural patterns (Spec 03). The embeddings open the door to gradient-based refinement of the graph structure itself (Spec 04) — the experiment the whole project is pointing toward.

**Why does the repository act as a standard library?** The graph repository stores the raw spec text for each verified compilation. `MarkdownSpecPass` retrieves prior compilations that used similar constructs and injects their specs into the extraction prompt. The LLM pattern-matches against verified examples instead of synthesizing from rules alone — this turned out to be the difference between reliable extraction and garbage output on novel construct combinations.

## Roadmap

| Spec | Title | Status |
|------|-------|--------|
| 41 | Node MLP training loop | Phase 2 gate — contrastive + type-classification loss |
| 44 | Repository health | Retroactive eviction + assertion-weighted retrieval |
| 42 | User-defined intrinsics | `intrinsics.json` + `call:` spec construct; unblocks OpenTK, Terminal.Gui |
| 43 | WASM backend | `--target wasm` → WAT generation + optional WASM assembly |
| 45 | Graph memory | Episodic → semantic repository: pattern nodes, cross-compilation edges, failure memory, abstract pattern injection |
| 20 | Roadmap to self-hosting | Long-horizon exploration |
| 21 | Direct graph extraction | Skip MarkdownSpecPass for well-formed inputs |
