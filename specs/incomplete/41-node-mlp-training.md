# Spec 41 — Node MLP Training Loop

**Status:** Not started
**Depends on:** Spec 04a ✓ (MLP forward pass and plumbing in place)
**Scope:** `IronLlm/Learning/NodeMlpTrainer.cs`; new `scripts/train.sh`; `geometry.py` comparison

## What this spec is

Phase 2 of the differentiable node MLP system. It implements offline weight updates using
the accumulated graph structure in the repository as the training signal. The goal is
measurable improvement in the similarity geometry from the Spec 35 baseline — within-cluster
similarities higher, between-cluster similarities lower.

## Training signal

Two self-supervised objectives derived from graph structure — no human annotation required:

### 1. Structural contrastive loss

For each `Contains` or `DependsOn` edge `(u, v)` in a compiled graph, the refined
embeddings of `u` and `v` should be more similar than the refined embeddings of `u` and a
randomly sampled unconnected node `w`:

```
L_contrastive = max(0, margin - cos(u, v) + cos(u, w))
```

`margin = 0.2` (tunable). Positive pairs: `Contains` and `DependsOn` edges only.
Negative pairs: random node from the same graph with no edge to `u`.

**CFG successor edges are excluded from training pairs.** `SuccessorTrue`/`SuccessorFalse`
edges encode execution flow and can form cycles (loop back-edges). Including them in the
contrastive loss would require backpropagating through recurrent paths — the same instability
that makes vanilla RNNs hard to train. The `Contains` DAG provides sufficient structural
signal without this risk.

### 2. Type classification loss

A node's refined embedding should predict its kind. A small linear probe
`Linear(1024 → num_kinds)` + softmax, trained jointly:

```
L_type = CrossEntropy(probe(refined_emb), node_kind_label)
```

Combined loss: `L = L_contrastive + 0.1 * L_type`

## Optimizer

Mini-batch SGD with momentum:
- Learning rate: 0.001
- Momentum: 0.9
- Batch size: 32 edge pairs per update
- Epochs: 10 per training run (re-runnable; weights accumulate across runs)

Gradient computation: manual backprop through the two-layer MLP. No autograd library.
The architecture is small enough that hand-derived gradients are tractable:

```
dL/dW2, dL/dB2  ←  output layer gradients
dL/dW1, dL/dB1  ←  hidden layer gradients (chain rule through ReLU)
```

## Implementation

### `IronLlm/Learning/NodeMlpTrainer.cs`

```csharp
public class NodeMlpTrainer
{
    public NodeMlpTrainer(NodeMlpRegistry registry, float lr = 0.001f, float momentum = 0.9f);

    // Run one training epoch over all graphs in the repository.
    public TrainingReport Train(IEnumerable<TrainingGraph> graphs, int epochs = 10);
}

public record TrainingGraph(SemanticGraph Graph, List<NodeEmbedding> Embeddings);
public record TrainingReport(int Epochs, int Batches, float FinalLoss, float InitialLoss);
```

### `scripts/train.sh` (and `scripts/train.py`)

Standalone offline training script. Does not require the full pipeline to run:

```bash
scripts/train.sh [--epochs N] [--lr F]
```

1. Load all `02-semantic-graph.json` + `03-embeddings.json` pairs from the repository.
2. Construct `TrainingGraph` objects.
3. Instantiate `NodeMlpRegistry.LoadOrCreate(repoPath)`.
4. Run `NodeMlpTrainer.Train(graphs, epochs)`.
5. Save updated weights to `repository/node-mlp-weights.json`.
6. Print loss curve (initial → final).

### Integration with pipeline

After training, the next pipeline run loads the updated weights via `NodeMlpPass`
and produces refined embeddings informed by the learned structure.
`RepositoryRetrievalPass` then uses these refined embeddings for similarity scoring
instead of the raw Ollama vectors.

Update `RepositoryRetrievalPass` to use `context.Embeddings` (post-refinement)
rather than raw embeddings loaded fresh from disk.

## Geometry comparison

After training, rerun `scripts/geometry.py`. Compare against:
- `repository/geometry-before-training.json` (Spec 04a baseline with random weights)
- The Spec 35 raw-embedding baseline

Expected improvement: BubbleSort↔SelectionSort should rise above BubbleSort↔FizzBuzz
(the one cluster check that failed in Spec 35). If it doesn't after 10 epochs on the
current repository size, log the result and increase epochs or repository size before
claiming success.

## Acceptance criteria

1. `scripts/train.sh` completes without error on the current repository.
2. `TrainingReport.FinalLoss < TrainingReport.InitialLoss` — the weights improve.
3. After training and a fresh pipeline run, `scripts/geometry.py` shows:
   - BubbleSort↔SelectionSort > BubbleSort↔FizzBuzz  (the failing Spec 35 check now passes)
   - No previously-passing cluster check regresses
4. All existing pipeline tests pass — `NodeMlpPass` is transparent when weights are random.

## Forward pointer — recurrent CFG layer

Once Spec 41 produces stable node MLP weights, those refined embeddings become fixed points
— the nodes' representations are no longer moving targets. At that point the CFG execution
graph becomes tractable to learn over.

The approach: treat the sequence of CFG blocks as a bounded input sequence to a recurrent
component (LSTM or GRU), where each block is represented by the mean of its nodes' stable
MLP-refined embeddings. The recurrence is over a finite execution trace, not through raw
mutable weights — so the recurrent gradient path is bounded by the trace length, not by
the graph topology.

The training signal already exists in the pipeline: `AcceptanceVerificationPass` produces a
binary pass/fail for every compilation. Each `(CFG trace, acceptance outcome)` pair in the
repository is a labelled training example. The recurrent layer would learn to predict whether
a given CFG shape, over a given set of refined node embeddings, produces a correct binary.

This is a future spec (not scoped here). Spec 41 is the prerequisite: stable embeddings are
required before the recurrent layer's input representation is meaningful.

## Open questions

- How many repository entries are needed before loss curves stabilize? Log per-epoch
  loss and repository size together so this becomes empirical rather than guessed.
- Should weight updates be applied per-compilation (online) or only via `train.sh`
  (offline)? Start offline — online updates risk instability with one new example at a time.
- Embedding drift logging: after each training run, compute the mean cosine distance
  between pre- and post-training refined embeddings across the repository. This quantifies
  how much the weights have moved and whether additional epochs are still doing work.
