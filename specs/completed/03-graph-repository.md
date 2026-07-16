# Spec 03 — Graph Repository

**Status:** Design  
**Scope:** New `Repository/` directory, changes to `CompilationContext`, new `RepositoryPass`

## Motivation

Today every run is stateless. The graph repository makes the compiler accumulate experience across compilations.

Instead of discarding intermediate representations after each run, persist the semantic graph and embeddings for every successful compilation. Every run adds new subgraphs — loops, branches, arithmetic patterns — along with their embeddings. Over time, the repository becomes a library of semantic building blocks. When a new specification arrives, the first step is not "generate from scratch" — it is "retrieve similar graphs."

## What Gets Stored

Each successful compilation persists a `CompiledUnit` to disk:

```json
{
  "id": "uuid",
  "specHash": "abc123...",
  "programName": "FizzBuzz",
  "compiledAt": "2026-07-16T09:00:00Z",
  "semanticGraphPath": "repository/abc123/02-semantic-graph.json",
  "embeddingsPath":    "repository/abc123/03-embeddings.json",
  "cfgPath":           "repository/abc123/04-cfg.json",
  "stackIrPath":       "repository/abc123/05-stackir.json",
  "msilPath":          "repository/abc123/06-program.il"
}
```

The repository index lives at `repository/index.json` — a list of `CompiledUnit` summaries. Full artifacts live in per-hash subdirectories.

## Retrieval

A `RepositoryRetrievalPass` runs after `EmbeddingPass`. It:

1. Takes the current compilation's node embeddings (already in `context.Embeddings`).
2. Loads all embeddings from the repository index.
3. Computes cosine similarity between each current node and all stored nodes. `SemanticNormalizationPass` already contains `CosineSimilarity` as a public static method — reuse it.
4. Surfaces the top-K most similar prior subgraphs and attaches them to `CompilationContext` as `SimilarPriors`.

Similarity threshold: start at 0.85, tune empirically.

`SimilarPriors` can inform any downstream pass that needs to reason about program structure. The first concrete use will be whatever LLM-driven pass is introduced next — the priors are included in its context as grounding: "Previously compiled similar patterns: Loop from FizzBuzz (similarity 0.94), Branch from FizzBuzz (similarity 0.91)."

## Cosine Similarity

Reuse `SemanticNormalizationPass.CosineSimilarity(float[], float[])` — already implemented and tested.

## Repository Location

`{workingDirectory}/repository/` — sibling to `examples/`. Configurable via CLI arg.

## Storing API Knowledge

> **DEFERRED — not implemented in Spec 03.**
> 
> Requires build-failure introspection: the recording policy depends on detecting that a compile failed with a missing-member or wrong-signature error and then succeeded after correction in the same session. That detection mechanism does not exist in the pipeline. This is a distinct design problem from graph storage and retrieval.
> 
> When this is revisited, write a separate spec. The `ApiKnowledgeRecord` shape below is preserved for reference.

The repository should also persist integration knowledge discovered at runtime — not just compiled graphs. When the compiler successfully integrates with an external API, it can record what it learned: the correct call shape, the version it was tested against, and the contract it satisfies.

### `ApiKnowledgeRecord` Shape (reference, not implemented)

```json
{
  "id": "uuid",
  "packageId": "System.CommandLine",
  "version": "2.0.10",
  "discoveredAt": "2026-07-16T12:00:00Z",
  "notes": "Option<T> ctor is (string name, string[] aliases). Description is a property. SetAction receives ParseResult; use result.GetValue(option) to extract values.",
  "embedding": [...]
}
```

The embedding is computed from `notes` so future queries retrieve it via cosine similarity even without the exact package name.

### Recording Policy (reference, not implemented)

API knowledge is recorded whenever a build fails due to a missing member or wrong signature, and the correct shape is subsequently discovered and the build succeeds. The record is written automatically after a successful compile that followed at least one API-related build failure in the same session.

## Open Questions

- Should the repository store only successful compilations, or all attempts? Suggested: successes only — the repository should be a library of known-good patterns.
- What is a "subgraph" for retrieval — individual nodes, or connected components? Start with individual nodes; connected components are a later optimization.
- How should `ApiKnowledgeRecord`s be scoped — per-package, per-type, or per-method? Start per-package with free-form notes.
