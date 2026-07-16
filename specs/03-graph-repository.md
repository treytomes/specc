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

## Open Questions

- Should the repository store only successful compilations (i.e. those that pass all invariants), or all attempts? Suggested: successes only — the repository should be a library of known-good patterns.
- Similarity threshold for surfacing a prior: start at 0.85, tune empirically.
- What is a "subgraph" for retrieval purposes — individual nodes, or connected components? Start with individual nodes; connected components are a later optimisation.
