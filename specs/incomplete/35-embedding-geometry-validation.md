# Spec 35 — Embedding Geometry Validation

**Status:** Incomplete
**Depends on:** Spec 03 (graph repository) ✓, Spec 34 (Collatz — enough program variety)

## What this spec is

Before implementing Spec 04 (differentiable node MLPs), validate the premise: do programs that implement the same intent land close in embedding space, and do programs with different intent land far apart?

This is the empirical foundation Spec 04 depends on. If the raw mxbai-embed-large embeddings already cluster well by program intent, the MLP refinement has a good starting point. If they don't, Spec 04 needs to address that first.

## Measurement

For each compiled program in the repository, compute pairwise cosine similarities between program-level embeddings (the `ProgramNode` embedding, or the mean of all non-assertion node embeddings).

Expected clusters:
- FizzBuzz / Fizz / FizzBuzzHundred should be closer to each other than to BubbleSort.
- BubbleSort and SelectionSort (both sorting programs) should be closer to each other than to FizzBuzz.
- Collatz and Fibonacci (both number sequences with conditional termination) may cluster together.

## Implementation

A new `scripts/geometry.sh` (or a standalone `IronLlm.Analysis` tool) that:

1. Loads all `03-embeddings.json` artifacts from the repository.
2. Computes a pairwise similarity matrix.
3. Prints a ranked similarity table.
4. Optionally writes a `geometry.json` summary that Spec 04 can use as a baseline.

No new compiler passes. No changes to the pipeline.

## Success criterion

At least two of the expected clusters are confirmed by the similarity matrix. If zero clusters are confirmed, the raw embeddings are not program-intent-sensitive and Spec 04 must address the representation before the MLP refinement can work.

## Connection to Spec 04

Spec 04 trains node MLPs to refine embeddings. The training signal is structural consistency (connected nodes should be more similar than disconnected ones). If the raw embeddings already cluster by intent, Spec 04's training is tuning a good baseline. If they don't, Spec 04's training is doing heavier lifting and the architecture needs to be designed accordingly.

This spec produces the measurement. Spec 04 interprets it and acts on it.
