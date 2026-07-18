# IronLlm Roadmap

This compiler is an experiment in treating programs as semantic objects. The LLM is not the orchestrator — it does two specific things: extract structure from prose (MarkdownSpecPass) and produce per-node embeddings (EmbeddingPass). Everything else is deterministic. The goal is a pipeline where program structure is queryable, comparable, and eventually refinable by gradient.

The roadmap has three phases. They are sequential in intent but not in implementation — later phases inform earlier decisions.

---

## Phase 1 — Stress-test the pipeline

The current pipeline handles: counted integer loops with divisibility branches, arithmetic on scalars, fixed-size integer arrays, and simple linear string I/O. These are enough to prove the architecture but not enough to reveal its limits.

Phase 1 adds examples that incrementally stress each dimension, with a specific goal: **find the extraction cliff** — the point where ministral-3b can no longer reliably translate prose into a valid spec.

| Spec | Example | New construct | Signal |
|------|---------|--------------|--------|
| 32 | Guesser | Int input, comparison branching | Does the model use `compare:` correctly, or invent syntax? |
| 33 | Calculator | Multiple inputs in sequence | Does multi-input ordering hold? Does `TestInput` scale to two lines? |
| 34 | Collatz | Unbounded `while:` loop, integer division | Expected extraction cliff. Records where and how the model fails. |

When the model fails on Spec 34 (or earlier), that failure is data, not a setback. It tells us exactly where the spec format has outgrown the extraction front-end, and whether Spec 21 is a prerequisite to continuing or an optimization.

**Extraction cliff hypothesis:** ministral-3b will handle Spec 32 and 33 reliably (incremental additions within its demonstrated competence window). It will fail on Spec 34 because `while:` + nested conditional assigns + integer division in a single prompt exceeds the reliable extraction window for a 3B model.

---

## Phase 2 — Validate the differentiable semantic graph

Before implementing learned node refinement (Spec 04), validate the premise with measurement.

| Spec | What it does |
|------|-------------|
| 35 | Embedding geometry validation — pairwise similarity matrix across all compiled programs. Confirms whether programs with the same intent cluster in embedding space. |
| 04 | Differentiable node MLPs — small per-node-type feed-forward networks that refine embeddings based on local graph context. Trained on the repository's accumulated compilations using structural contrastive loss. |

Spec 35 produces a baseline. Spec 04 is only worth building if the baseline shows that the raw embeddings are program-intent-sensitive (i.e., the clusters exist even before refinement). If they don't, the embedding architecture needs to be revisited before training.

**Spec 04 does not improve extraction reliability.** Its value is longer-horizon: once embeddings cluster reliably by intent, it enables program synthesis by analogy — embed a prose description, retrieve the closest prior graph, adapt it by gradient toward the new acceptance criteria. That path bypasses the extraction front-end entirely. But it requires Spec 35 first, and a gradient-based graph adaptation mechanism that is not yet specced.

---

## Phase 3 — Turing-completeness and self-hosting

| Spec | What it does |
|------|-------------|
| 34 | Collatz (carried from Phase 1) — unbounded loop crosses the Turing-completeness boundary |
| 20 | Roadmap to self-hosting — long-horizon exploration of expressing the compiler's own passes in the spec language |

Self-hosting is not a near-term deliverable. It is a horizon that shapes decisions about language expressiveness. The question to revisit periodically: does the spec format as it exists today have the primitives needed to express, say, `SemanticGraphPass`? The answer informs what to add and what to leave out.

---

## The extraction cliff and Spec 21

Every new construct added to the spec format grows the LLM system prompt and reduces extraction reliability. The `.spec` text format has a ceiling: ministral-3b's ability to correctly apply all available constructs degrades as the prompt grows. We have already seen it invent syntax (`default_output:`) and ignore rules.

Spec 21 replaces the two-step Markdown → `.spec` → graph path with a single structured LLM call that produces the graph JSON directly. Three attempts have been made, all blocked:

- **Attempt 1 (2026-07-16):** plain JSON. ~13 minutes for FizzBuzz. Wrong topology (two Modulo nodes instead of one Modulo(15)).
- **Attempt 2 (2026-07-17):** JSON with `ChatOptions.ResponseFormat` schema constraint + worked examples. 7+ minutes. Topology correct, but model omitted required type-specific fields, producing null-reference crashes. Speed unchanged.
- **Attempt 3 / YAML spike (2026-07-17):** YAML format to reduce token count. 6m 51s, **4039 output tokens** — the model generated the graph twice (self-corrected mid-output), inflating token count beyond even the JSON attempts. Invalid UUIDs, hallucinated nodes, format-instruction violations.

**Conclusion:** ministral-3b cannot reliably produce a complete, well-formed graph for FizzBuzz-complexity programs regardless of intermediate format. The bottleneck is model capacity, not format. Spec 21 is deferred until a larger extraction model (≥7B) is available locally.

Spec 21 remains the correct architectural direction. Its value scales with the graph type system: adding `WhileLoopNode` means one schema entry and one prompt example, not a new spec keyword and parser branch. It just requires a model that can hold the schema and the input context simultaneously without degrading.

---

## Decision gate after Phase 1

After Specs 32, 33, and 34:

- **If the model fails on or before Spec 34:** the extraction cliff is confirmed. Document the failure mode. Spec 21 remains deferred (model capacity, not format). The next path is either: (a) switch to a larger local model (mistral:7b is available), or (b) proceed to Phase 2 with the examples that do compile.
- **If the model handles Spec 34 reliably:** run Spec 35 (geometry validation), then implement Spec 04. The extraction path is still viable; the next leverage point is the learned representation.

---

## What "complete" means

The compiler is "complete" in the experiment's terms when:

1. Programs with the same intent demonstrably cluster in embedding space (Spec 35 confirmed).
2. Node MLP refinement improves that clustering measurably over raw embeddings (Spec 04 validated).
3. An unbounded loop compiles and runs correctly (Spec 34 — Turing-completeness).
4. The extraction front-end is not the bottleneck — either because the model handles the full language, or because Spec 21 has replaced it.

Self-hosting (Spec 20) is beyond "complete" — it is the experiment's long horizon.
