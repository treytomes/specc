# Spec 03 — Graph Repository

**Status:** Design  
**Scope:** New `Repository/` directory, changes to `CompilationContext`, new `RepositoryPass`

## Motivation

From the conversation:

> Instead of the compiler simply discarding its intermediate representations, keep them in a graph repository. Every compilation adds new subgraphs — loops, branches, arithmetic patterns — along with their embeddings and successful lowerings. Over time, the repository becomes a library of semantic building blocks. When a new specification arrives, the first step isn't "generate from scratch." It's "retrieve similar graphs."

Today every run is stateless. The graph repository makes the compiler accumulate experience across compilations.

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

## Retrieval Pass

A new `RepositoryRetrievalPass` runs before `CfgPass`. It:

1. Embeds the current spec's semantic graph nodes (already computed by `EmbeddingPass`).
2. Loads all embeddings from the repository index.
3. Computes cosine similarity between each current node's embedding and all stored node embeddings.
4. Surfaces the top-K most similar prior subgraphs and attaches them to `CompilationContext` as `SimilarPriors`.

`CfgPass` then includes the retrieved priors in its prompt:

```
Previously compiled similar patterns:
  - Loop node from "Countdown" (similarity 0.94): [lowered as ...]
  - Branch node from "FizzBuzz" (similarity 0.99): [lowered as ...]
```

This shifts the LLM from synthesis to adaptation — it's shown what worked before.

## Cosine Similarity

```csharp
static float CosineSimilarity(float[] a, float[] b)
{
    float dot = 0, magA = 0, magB = 0;
    for (int i = 0; i < a.Length; i++)
    {
        dot  += a[i] * b[i];
        magA += a[i] * a[i];
        magB += b[i] * b[i];
    }
    return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
}
```

No external vector database needed for this scale.

## Repository Location

`{workingDirectory}/repository/` — sibling to `Artifacts/`. Configurable via CLI arg.

## Storing API and Integration Knowledge

The repository should also persist *integration knowledge* discovered at runtime — not just compiled graphs. When the compiler successfully integrates with an external system (an SDK, an Ollama API, a .NET reflection API), it can record what it learned: the correct call shape, the version it was tested against, and the contract it satisfies.

### Motivation

During development of Spec 09 (CLI), the correct `System.CommandLine` 2.0 API was not known in advance. The compiler had to probe the assembly via reflection to discover that `Option<T>` takes `(string name, string[] aliases)` — not a named `description:` parameter — and that `SetAction` receives a `ParseResult` rather than individual typed arguments. This is non-obvious, version-specific knowledge that took multiple build-fail-inspect cycles to establish.

If that learning were stored in the repository as a structured `ApiKnowledgeRecord`, a future compilation (or a future session of the compiler itself) could retrieve it before attempting to generate code that calls the same package.

### `ApiKnowledgeRecord` Shape

```json
{
  "id": "uuid",
  "packageId": "System.CommandLine",
  "version": "2.0.10",
  "discoveredAt": "2026-07-16T12:00:00Z",
  "notes": "Option<T> ctor is (string name, string[] aliases). Description is a property. SetAction receives ParseResult; use result.GetValue(option) to extract values. InvokeAsync is on ParseResult, not RootCommand.",
  "embedding": [...]
}
```

The embedding is computed from `notes` — so that future queries like "how do I use System.CommandLine options" surface this record via cosine similarity, even if the exact package name isn't in the query.

### Retrieval at Code-Generation Time

Any future LLM-driven pass (Spec 10 Markdown ingestion, Spec 12 Semantic Normalization) that generates code calling an external API first queries the repository for relevant `ApiKnowledgeRecord`s and includes them in its prompt as grounding. This makes the LLM an informed adapter rather than a guesser.

### Recording Policy

API knowledge is recorded whenever:
1. A build fails due to a missing member or wrong signature, AND
2. The correct shape is subsequently discovered and the build succeeds.

The record is written automatically by the pipeline after a successful compile that followed at least one API-related build failure in the same session. Manual records can also be added via a CLI command (`ironllm knowledge add --package ... --note ...`).

## Open Questions

- Should the repository store only successful compilations (i.e. those that pass all invariants), or all attempts? Suggested: successes only — the repository should be a library of known-good patterns.
- Similarity threshold for surfacing a prior: start at 0.85, tune empirically.
- What is a "subgraph" for retrieval purposes — individual nodes, or connected components? Start with individual nodes; connected components are a later optimization.
- How should `ApiKnowledgeRecord`s be scoped — per-package, per-type, or per-method? Start per-package with free-form notes; structured per-method records are a later refinement.
