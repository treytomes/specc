# Spec 04 — Differentiable Node MLPs

**Status:** Research / Exploratory  
**Scope:** New `Learning/` directory

## Motivation

From the conversation:

> Instead of a giant transformer, imagine thousands of specialized graph nodes. Loop Node — small MLP. Modulo Node — small MLP. Each learns its own behavior. The graph determines how they're connected. Structure becomes persistent while computation becomes adaptable.

Today every node is a pure data record. This spec explores giving each node type its own small learned component — a tiny MLP that refines the node's embedding based on its local graph context (its immediate neighbors and edge types).

This is distinct from the Ollama embedding pass, which produces static per-node vectors. A node MLP is trained and updates its parameters.

## What a Node MLP Does

Each node type (Loop, Branch, Modulo, Print, Variable) gets a small feed-forward network:

```
Input:  concatenation of [node's static embedding, mean of neighbor embeddings]
Hidden: one layer, ReLU
Output: refined embedding (same dimension as input embedding)
```

The refined embedding is used downstream for similarity retrieval (Spec 03) instead of the raw Ollama embedding.

Over many compilations the node MLPs learn what "Loop" means in the context of this compiler's graph structure — not just what mxbai-embed-large thinks "loop" means in general text.

## Training Signal

We don't have labels. The training signal comes from structural consistency:

- Two nodes connected by a `Contains` edge should have more similar refined embeddings than two unconnected nodes (contrastive loss).
- A node's refined embedding should predict its type (cross-entropy on the node kind enum).

This is unsupervised / self-supervised — no human annotation required.

## Scope Constraints

This spec is exploratory. It does not require integrating a full ML training framework. A reasonable proof of concept:

1. Implement node MLPs as plain `float[][]` weight matrices in C#.
2. Forward pass only (inference) initially — use pre-trained random weights to verify the plumbing.
3. A separate offline training script (or a future `TrainingPass`) updates the weights using the repository's accumulated embeddings.

The weights are persisted to `repository/node-mlp-weights.json`.

## Relation to DLN Discussion

The conversation drew a line from "AST with learned node representations" to Differentiable Logic Networks. This spec is the first step on that line — not a full DLN, but the foundation that makes one possible. The key property to preserve: **graph structure is persistent and explicit; learned behavior lives in the node parameters, not in the weights of a monolithic transformer**.

## Open Questions

- Embedding dimension from mxbai-embed-large is 1024. Hidden layer size? Start with 256.
- How many compilations are needed before the MLPs diverge meaningfully from random? Empirical question — instrument and log embedding drift per compilation.
- Should node MLPs share weights across same-kind nodes (one MLP per `NodeKind`) or be per-node-instance? Start per-kind; per-instance is a later experiment.
