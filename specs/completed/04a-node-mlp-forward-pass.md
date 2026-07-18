# Spec 04a — Node MLP Forward Pass

**Status:** Completed
**Depends on:** Spec 35 ✓ (geometry baseline confirmed — 2/3 cluster checks pass)
**Blocks:** Spec 41 (training loop — needs the plumbing in place first)
**Scope:** New `IronLlm/Learning/` directory; new `NodeMlpPass.cs`; `geometry.py` rerun

## What this spec is

Phase 1 of the differentiable node MLP system. It implements the MLP architecture and wires
it into the pipeline as a forward-pass-only inference step. Weights are initialized randomly
and persisted. The goal is verified plumbing — the pipeline produces refined embeddings and
`geometry.py` confirms the refined embeddings are no worse than raw.

Training (weight updates) is Spec 41.

## Architecture

Each node kind gets one small feed-forward network shared across all nodes of that kind:

```
Input:  [node_embedding(1024) ∥ mean_neighbor_embedding(1024)]  →  2048 floats
Hidden: Linear(2048 → 256) + ReLU
Output: Linear(256 → 1024)                                      →  1024 floats (refined embedding)
```

One MLP per kind: `Program`, `Loop`, `Branch`, `Print`, `Modulo`, `Variable`, `Assign`,
`Input`, `WhileLoop`, `Comparison`, `Array`. Unknown kinds pass through unchanged.

Neighbor embeddings: only nodes connected via **`Contains`** or **`DependsOn`** edges
pointing *away* from the current node (i.e. nodes this node structurally contains or depends
on). The semantic graph's `Contains` hierarchy is a DAG by construction — program → loop →
branch → print — so this aggregation is always acyclic. CFG successor edges (`SuccessorTrue`,
`SuccessorFalse`) are **excluded**: they encode execution flow and can form cycles (loop
back-edges), which would make gradient computation recurrent and unstable during Spec 41
training. Self is excluded. If a node has no qualifying neighbors, the neighbor mean is a
zero vector.

## Implementation

### `IronLlm/Learning/NodeMlp.cs`

Plain `float[][]` weight matrices — no ML framework dependency:

```csharp
public class NodeMlp
{
    public float[][] W1 { get; init; }  // [256][2048]
    public float[]   B1 { get; init; }  // [256]
    public float[][] W2 { get; init; }  // [1024][256]
    public float[]   B2 { get; init; }  // [1024]

    public float[] Forward(float[] nodeEmb, float[] neighborMean);
}
```

`Forward` computes: `W2 * ReLU(W1 * [nodeEmb ∥ neighborMean] + B1) + B2`.

### `IronLlm/Learning/NodeMlpRegistry.cs`

Owns one `NodeMlp` per kind. Loads from / saves to `repository/node-mlp-weights.json`.
On first run (no weights file), initializes with Xavier uniform: `±sqrt(6 / (fan_in + fan_out))`.

```csharp
public class NodeMlpRegistry
{
    public static NodeMlpRegistry LoadOrCreate(string repositoryPath);
    public void Save(string repositoryPath);
    public float[] Refine(Node node, float[] rawEmbedding, float[] neighborMean);
}
```

### `IronLlm/Passes/NodeMlpPass.cs`

New pass, runs after `EmbeddingPass` (03) and before `SemanticNormalizationPass` (03b):

```
Name:         "03a-NodeMlp"
ArtifactFile: "03a-refined-embeddings.json"
```

For each node in the graph that has a raw embedding in `context.Embeddings`:
1. Look up neighbor node IDs via the graph edges.
2. Gather their raw embeddings from `context.Embeddings`.
3. Compute the neighbor mean vector (zero vector if none).
4. Call `registry.Refine(node, rawEmb, neighborMean)`.
5. Replace the embedding in `context.Embeddings` with the refined vector.

The pass operates in-place on `context.Embeddings` — downstream passes
(`SemanticNormalizationPass`, `RepositoryRetrievalPass`) see refined vectors automatically.

### Pipeline registration

Register `NodeMlpPass` between `EmbeddingPass` and `RepositoryRetrievalPass` in `Program.cs`.

## Geometry check

After implementation, delete all artifacts except `03-embeddings.json` for one program
and rerun `scripts/geometry.py`. The refined geometry should not be worse than the Spec 35
baseline. With random weights it will likely be similar (random linear transformations
approximately preserve cosine similarity structure). That is the expected result for Phase 1 —
it confirms the plumbing is correct without claiming the weights have learned anything.

Record the baseline in `repository/geometry-before-training.json` for comparison with
Spec 41's post-training geometry.

## Acceptance criteria

1. `dotnet build` succeeds — no ML framework dependencies added.
2. All existing tests pass.
3. `scripts/geometry.py` runs on the repository and produces results consistent with
   the Spec 35 baseline (within ±0.02 on all pairs — random init should not significantly
   distort the space).
4. `repository/node-mlp-weights.json` is written after the first pipeline run.
5. A second pipeline run (incremental) loads weights from disk and skips re-initialization.

## What this does NOT include

- Weight updates / gradient computation — that is Spec 41.
- Any change to the acceptance verification or CFG pass behavior.
- Integration with `RepositoryRetrievalPass`'s similarity scoring — that is deferred to
  Spec 41 once weights are meaningful.
