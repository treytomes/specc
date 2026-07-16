# IronLlm — CLAUDE.md

## What this project is

IronLlm is a spec compiler: it takes a structured `.spec` file describing a program and lowers it through a series of deterministic passes to a native executable, with an LLM-powered embedding pass that adds semantic metadata to the intermediate graph.

The architecture mirrors a conventional compiler (parse → semantic analysis → IR lowering → code emit) but adds a semantic graph layer where each node carries a learned embedding. This is an exploration of the ideas in the "living computer / differentiable semantic graph" conversation: what happens when program structure is treated as a first-class persistent graph rather than flat source text?

## Project layout

```
IronLlm/
  Graph/          Node, Edge, CfgBlock, StackInstruction type definitions
  Passes/         One file per compiler pass + CompilationContext + ArtifactWriter
  Program.cs      Incremental pipeline runner
examples/
  FizzBuzz/       FizzBuzz.spec + artifacts/ (generated, gitignored)
scripts/
  install.sh      Dependency check and setup
  build.sh        dotnet build wrapper
  run.sh          Run the compiler (defaults to FizzBuzz example)
  test.sh         End-to-end smoke test
  assemble.sh     Optional ilasm step for 06-program.il
specs/            Design specs for upcoming passes (Markdown)
```

## Compiler passes

Each pass implements `ICompilerPass` (`Name`, `ArtifactFile`, `ExecuteAsync`, `LoadFromArtifactAsync`). The pipeline is **incremental**: if a pass's artifact file already exists in the output directory, the pass is skipped and context is loaded from disk. Deleting an artifact forces that pass and all downstream passes to re-run.

| # | Pass | Artifact | How |
|---|------|----------|-----|
| 01 | `ParseSpecPass` | `01-spec.json` | Read `.spec` file |
| 02 | `SemanticGraphPass` | `02-semantic-graph.json` | Deterministic parser → typed node/edge graph |
| 03 | `EmbeddingPass` | `03-embeddings.json` | mxbai-embed-large via Ollama — one call per node |
| 04 | `CfgPass` | `04-cfg.json` | Deterministic structural lowering from semantic graph |
| 05 | `StackIrPass` | `05-stackir.json` | Pattern-match CFG instructions → stack opcodes |
| 06 | `MsilGenerationPass` | `06-program.il` | Stack IR → IL assembly text |
| 07 | `AssemblyEmitPass` | `07-program.dll` + `{Name}` | `PersistedAssemblyBuilder` → patched apphost launcher |

## The `.spec` file format

```
program: <Name>

loop:
  from: <int>
  to: <int>

branch:
  condition: <snake_case>
  divisor: <int>           # omit for the default branch
  true_output: "<string>"  # quoted string, or {variable}

variable:
  name: <identifier>
  type: <type>
```

## Running

```bash
scripts/install.sh          # verify/install dependencies
scripts/run.sh              # compile examples/FizzBuzz/FizzBuzz.spec
scripts/run.sh path/to/spec # compile a different spec
scripts/test.sh             # build + full pipeline + output assertions
```

The compiled executable is written to `<artifacts-dir>/<ProgramName>` and is directly runnable.

## Dependencies

- .NET 10 SDK
- Ollama running on `http://localhost:11434`
- Models: `mxbai-embed-large:latest` (embeddings), `ministral-3:3b` (reserved for future LLM passes)
- python3 (used by `test.sh` for JSON artifact inspection only)

`scripts/install.sh` checks all of these and pulls missing Ollama models automatically.

## Key design decisions

**LLM as one optional pass, not the orchestrator.** Embeddings are metadata on the graph — they don't change structure. The CFG is built deterministically. The LLM is reserved for tasks that require semantic understanding (e.g. Spec 10: Markdown spec ingestion).

**Incremental by artifact, not by timestamp.** Each pass owns a filename. If the file exists, the pass is skipped. This makes re-runs after a spec edit skip the expensive embedding pass automatically.

**apphost patching in-process.** `AssemblyEmitPass` uses `PersistedAssemblyBuilder` for PE emission and patches the SDK's `apphost` binary to produce a directly executable launcher — no `ilasm`, no `dotnet publish`, no shell-out.

## Upcoming work

See `specs/` for detailed design docs. Priority order:

1. `specs/01` — Fix StackIR pattern matching so `06-program.il` assembles correctly with ilasm
2. `specs/06` — Semantic validation pass (invariant checking at each stage)
3. `specs/08` — `.env` config (Ollama endpoint, model names)
4. `specs/09` — `System.CommandLine` + hosting + DI + logging
5. `specs/10` — Markdown spec ingestion (LLM extracts `.spec` from prose)
6. `specs/02` — Artifact hashing / manifest
7. `specs/07` — Graph visualization (Mermaid + SVG)
8. `specs/03` — Graph repository (persist and retrieve prior compilations)
9. `specs/04` — Differentiable node MLPs
10. `specs/05` — Second example: BubbleSort
