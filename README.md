# IronLlm

A proof-of-concept spec compiler. You describe a program as a structured specification; IronLlm compiles it through a multi-pass pipeline into a native executable — with an LLM-powered semantic embedding layer at the heart of the intermediate representation.

## The idea

Conventional compilers work on source text. IronLlm explores what happens when you treat program *intent* as the primary artifact: a typed semantic graph where every node carries a learned embedding that captures what it *means*, not just what it syntactically *is*. The compiler passes lower this graph — through CFG, stack IR, and MSIL — into a runnable binary, like a miniature LLVM.

This is a testbed for the idea that the gap between "what a human wants" and "what a machine executes" can be bridged by a compiler that understands both layers.

## Pipeline

Seven passes, each writing a named artifact file to an output directory. The pipeline is **incremental**: if an artifact already exists, the pass is skipped and context is loaded from disk.

```
FizzBuzz.spec
    │
    ▼ ParseSpecPass ─────────── 01-spec.json          raw spec text
    ▼ SemanticGraphPass ──────── 02-semantic-graph.json  typed node/edge graph
    ▼ EmbeddingPass ─────────── 03-embeddings.json     per-node vectors (LLM)
    ▼ CfgPass ───────────────── 04-cfg.json            control-flow graph
    ▼ StackIrPass ───────────── 05-stackir.json        stack machine IR
    ▼ MsilGenerationPass ─────── 06-program.il         IL assembly text
    ▼ AssemblyEmitPass ──────── 07-program.dll         managed PE
                       └──────── FizzBuzz              native launcher (executable)
```

The LLM (mxbai-embed-large via Ollama) is used exactly once — the embedding pass — where it genuinely earns its place: understanding the semantic meaning of each graph node. Everything else is deterministic.

## Quickstart

```bash
# 1. Install dependencies (checks .NET 10, Ollama, models; installs what's missing)
./scripts/install.sh

# 2. Compile the FizzBuzz example
./scripts/run.sh

# 3. Run the output
./examples/FizzBuzz/artifacts/FizzBuzz
```

Or run the full test suite:
```bash
./scripts/test.sh
```

## The spec format

Programs are described in a simple key-value DSL:

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

## Project structure

```
IronLlm/
  Graph/          Node.cs, Edge.cs, CfgBlock.cs, StackInstruction.cs
  Passes/         One file per pass + CompilationContext + ArtifactWriter
  Program.cs      Incremental pipeline runner
examples/
  FizzBuzz/       FizzBuzz.spec + artifacts/ (generated)
scripts/
  install.sh      Dependency setup
  build.sh        dotnet build
  run.sh          Run the compiler
  test.sh         End-to-end smoke test (21 checks)
  assemble.sh     Optional: re-assemble 06-program.il with ilasm
specs/            Design specifications for upcoming passes
```

## Prerequisites

| Dependency | Version | Purpose |
|------------|---------|---------|
| .NET SDK   | 10.0+   | Build and run |
| Ollama     | latest  | LLM inference host |
| mxbai-embed-large | latest | Embedding pass |
| ministral-3:3b | latest | Reserved for future LLM passes |

`scripts/install.sh` handles all of these. For Ollama's website, see the [Ollama docs](https://ollama.com).

## How the incremental pipeline works

Each `ICompilerPass` declares an `ArtifactFile` (e.g. `"04-cfg.json"`). Before executing a pass, the runner checks if that file exists in the artifacts directory. If yes, it loads the pass's output from disk and moves on. This means:

- The expensive embedding pass (one Ollama call per graph node) only runs once per spec.
- You can edit a downstream pass, delete its artifact, and re-run — only that pass and anything below it re-executes.
- Deleting `04-cfg.json` through `07-program.dll` re-lowers the graph without re-embedding.

## Roadmap

The `specs/` directory contains detailed design documents for upcoming work:

| Spec | Title | Status |
|------|-------|--------|
| 01 | StackIR pattern tightening | In progress |
| 02 | SHA-256 artifact manifest | Ready |
| 03 | Graph repository (persist + retrieve) | Design |
| 04 | Differentiable node MLPs | Research |
| 05 | BubbleSort — second example program | Ready |
| 06 | Semantic validation pass | Ready |
| 07 | Graph visualization (Mermaid + SVG) | Ready |
| 08 | .env configuration | Ready |
| 09 | System.CommandLine + DI + logging | Ready |
| 10 | Markdown spec ingestion | Ready |

## Architecture notes

**Why deterministic CFG?** An earlier version called a 3B LLM to generate the control-flow graph from the semantic graph. The problem: constructing correct CFG from a typed graph is a mechanical transformation, not a semantic understanding task. The 3B model couldn't follow structural constraints reliably. Rewriting `CfgPass` as deterministic code eliminated the entire class of errors immediately.

**Why apphost patching?** `AssemblyEmitPass` uses `PersistedAssemblyBuilder` (.NET 9+) to emit the managed PE entirely in-process, then copies the SDK's `apphost` binary and patches its filename placeholder to produce a directly-runnable native launcher — no `dotnet publish`, no `ilasm`, no shell-out.

**Why a semantic graph at all?** It's the persistence layer for program intent. Once a program has been compiled and its graph + embeddings are saved, future compilations of *similar* programs can retrieve and reuse prior structural patterns (Spec 03). The embeddings also open the door to gradient-based refinement of the graph structure itself (Spec 04) — the experiment this whole project is pointing toward.
