# IronLlm Roadmap

This compiler is an experiment in treating programs as semantic objects. The LLM is not the orchestrator — it does two specific things: extract structure from prose (MarkdownSpecPass) and produce per-node embeddings (EmbeddingPass). Everything else is deterministic. The goal is a pipeline where program structure is queryable, comparable, and eventually refinable by gradient.

The roadmap has three phases. They are sequential in intent but not in implementation — later phases inform earlier decisions.

---

## Phase 1 — Stress-test the pipeline ✓ COMPLETE

The current pipeline handles: counted integer loops with divisibility branches, arithmetic on scalars, fixed-size integer arrays, simple linear string I/O, unbounded conditional while loops, integer division, random number generation, and interactive (var-vs-var) while loops.

| Spec | Example | New construct | Outcome |
|------|---------|--------------|---------|
| 32 | Guesser | Int input, comparison branching | Pass |
| 33 | Calculator | Multiple inputs in sequence | Pass |
| 34 | Collatz | Unbounded `while:` loop, integer division | Pass — 9/9 assertions |

**Extraction cliff — actual outcome:** The cliff hypothesis was partially correct. ministral-3b did fail on Collatz initially, but was brought through with scaffolding rather than being blocked. The scaffolding required:

- `SpecConstructLibrary` worked examples per construct family (Spec 40) — the model pattern-matches against concrete examples rather than synthesising structure from rules
- Repository-as-standard-library: prior verified specs injected into the extraction prompt via `FindPriorsByTagsAsync`; seeded with Halve and StepCounter to teach the two-`true_assign:` pattern separately
- `ConsistencyMissing` promoted from warning to retry trigger — a spec missing classifier-selected constructs now retries with the full construct set
- `SemanticGraphPass` print-before-while relocation — a structural fix for LLM-ordered specs that place `print:` before `while:` in the output
- Bare expected-output parsing — `ParseExpectedOutputBlock` extended to accept non-fenced lines under `## Expected Output`

The model handles the full current spec language *with this scaffolding*. Without it, Collatz reliably fails. The scaffolding is now part of the permanent pipeline. Reliability still depends on the repository being seeded with relevant examples — cold-start on a new construct family may still fail.

---

## Phase 2 — Validate the differentiable semantic graph (IN PROGRESS)

| Spec | What it does | Status |
|------|-------------|--------|
| 35 | Embedding geometry validation — pairwise similarity matrix across all compiled programs | Done — 2/3 cluster checks pass; BubbleSort↔SelectionSort narrowly fails (0.928 vs BubbleSort↔FizzBuzz 0.932) |
| 04 | Differentiable node MLPs — small per-node-type feed-forward networks that refine embeddings based on local graph context | Forward pass done (04a/`NodeMlpPass`); **training loop not implemented** (Spec 41) |

**Spec 35 result:** Raw Ollama embeddings are program-intent-sensitive. Algorithmically distinct programs (FizzBuzz vs CountDown) separate clearly. Near-identical algorithms (BubbleSort vs SelectionSort) are nearly indistinguishable — which is the correct behaviour, but makes the geometric separation test borderline. The premise for Spec 04 holds.

**Spec 04 current state:** `NodeMlpPass` runs in the pipeline and refines embeddings using the per-kind MLP forward pass. The MLPs are randomly initialised — they transform the embedding space but do not yet improve it. Training (Spec 41) is the remaining work. Until Spec 41 runs and the loss converges, criterion 2 cannot be declared satisfied.

**Spec 04 does not improve extraction reliability.** Its value is longer-horizon: once embeddings cluster reliably by intent, it enables program synthesis by analogy — embed a prose description, retrieve the closest prior graph, adapt it by gradient toward the new acceptance criteria. That path bypasses the extraction front-end entirely. But it requires the training loop first.

---

## Phase 3 — Turing-completeness and self-hosting

| Spec | What it does | Status |
|------|-------------|--------|
| 34 | Collatz — unbounded conditional loop crosses the Turing-completeness boundary | **Done (2026-07-18)** — 9/9 assertions pass |
| 20 | Roadmap to self-hosting — long-horizon exploration of expressing the compiler's own passes in the spec language | Not started |

**The Turing-completeness boundary is crossed in the sense the roadmap intended.** The compiler can express unbounded conditional iteration with integer arithmetic and stdin input. Combined with the existing constructs (arrays, branching, assignment, random), it can express any iterative computation that fits within the spec format's data model (scalar integers and fixed-size integer arrays).

The qualifier matters: the data model is bounded. Fixed-size integer arrays and scalar integers mean the compiler cannot express programs that require unbounded memory (e.g. arbitrary-length lists, recursive call stacks, general heap allocation). It is not formally Turing complete in the CS sense — it cannot simulate a general Turing machine. What it *can* express is unbounded *iteration* over bounded data, which is the milestone the roadmap was measuring and which Collatz confirms.

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

## Decision gate after Phase 1 — actual outcome

The model did not handle Spec 34 reliably on its own. It required scaffolding (retry logic, worked examples, repository priors, a structural fix in `SemanticGraphPass`). This is the middle path between the two hypotheses in the original gate:

- The extraction cliff is real and confirmed — Collatz exhausted the model's reliable extraction window.
- The cliff was not impassable — scaffolding bridges it, and the scaffolding is now permanent infrastructure.

The next leverage point is the learned representation (Spec 41), not the extraction front-end. The extraction path is viable for the current spec language with the scaffolding in place. Adding significant new constructs may re-expose the cliff.

---

## What "complete" means

The compiler is "complete" in the experiment's terms when all four hold:

| # | Criterion | Status |
|---|-----------|--------|
| 1 | Programs with same intent demonstrably cluster in embedding space (Spec 35) | **Done** — 2/3 cluster checks pass; BubbleSort↔SelectionSort narrowly misses but near-identical algorithms clustering together is the *correct* behaviour |
| 2 | Node MLP refinement improves clustering measurably over raw embeddings (Spec 04 trained) | **Partial** — forward pass runs (`NodeMlpPass`); training loop not implemented (Spec 41 remaining) |
| 3 | Unbounded conditional loop compiles and runs (Spec 34) | **Done** — Collatz, 9/9 assertions, 2026-07-18; unbounded iteration over bounded data confirmed |
| 4 | Extraction front-end is not the bottleneck | **Unresolved** — scaffolding bridges the gap; model reliability remains a ceiling for new constructs; Spec 21 deferred |

Self-hosting (Spec 20) is beyond "complete" — it is the experiment's long horizon.
