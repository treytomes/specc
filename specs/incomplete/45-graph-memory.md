# Spec 45 — Graph Memory: From Episodic to Semantic Repository

## Status

Incomplete.

## Context

The current repository is episodic: each `CompiledUnit` is a specific compilation event — a program name, a raw spec text, a set of tags, an assertion count. Retrieval is a tag-overlap query that returns matching instances. Nothing generalises across entries.

This spec investigates what it would mean for the repository to have *semantic* memory — to extract abstract structure from accumulated episodes rather than just storing them. The goal is not to replace episodic storage but to build a second layer on top of it: a graph whose nodes are structural patterns, whose edges are relationships between those patterns, and whose entries are grounded in the episodic records that attest them.

This is a research spec. The implementation is phased: Phase A is purely additive (no changes to the existing pipeline), and each subsequent phase is optional and gated on the previous one working.

---

## The core idea

Three observations motivate this:

1. **Patterns recur across compilations.** Every interactive program uses a `while:` loop with `source: stdin`. Every sorting program uses nested loops and `assign:` blocks. The repository accumulates instances of these patterns but has no representation of the pattern itself.

2. **Relationships between compilations are implicit.** GuessingGame and SimpleGuess both use the interactive-while pattern, but GuessingGame adds `random:` on top. That relationship — "same base structure, additional construct" — exists nowhere in the repository and cannot be queried.

3. **Retrieval matches instances, not abstractions.** When compiling a new program that uses an interactive while loop, the retrieval pass finds GuessingGame or SimpleGuess by tag overlap. It cannot find "the general interactive-while pattern attested by multiple compilations" because that node does not exist.

Graph memory reifies these patterns as explicit nodes and makes the relationships between them queryable.

---

## Phase A — Pattern extraction (read-only, no pipeline changes)

### Goal

Produce a `repository-graph.json` alongside the existing `index.json`. This file is built offline (not during compilation) by scanning the episodic index and extracting recurring subgraph patterns.

### Pattern node types

| Pattern | Definition | Attested by |
|---------|-----------|-------------|
| `BoundedLoop` | `loop:` construct, fixed `from`/`to` | FizzBuzz, Fibonacci, BubbleSort, ... |
| `UnboundedLoop` | `while:` construct, no static bound | Collatz, GuessingGame, SimpleGuess |
| `DivisorBranch` | `branch:` with `divisor:` field | FizzBuzz, Fizz, FizzBuzzHundred |
| `ComparisonBranch` | `branch:` with `compare:` and `value:` or `compare_with:` | Guesser, GuessingGame |
| `ArithmeticKernel` | One or more `assign:` blocks with `op:` | Fibonacci, Collatz, BubbleSort |
| `StdinInput` | `variable:` with `source: stdin` | Guesser, Calculator, Collatz, GuessingGame |
| `RandomSource` | `random:` construct | GuessingGame, DiceRoll |
| `InteractiveLoop` | `UnboundedLoop` + `StdinInput` co-occurring | GuessingGame, SimpleGuess, Collatz |

Pattern nodes are extracted by scanning `SpecText` in each `CompiledUnit`. A pattern is considered attested when it appears in at least two distinct verified compilations.

### Edge types

| Edge | Meaning |
|------|---------|
| `AttestededBy` | Pattern → CompiledUnit (which episodes contain this pattern) |
| `Composes` | Pattern → Pattern (this pattern always co-occurs with another) |
| `Extends` | Pattern → Pattern (this pattern adds a construct on top of another) |
| `Conflicts` | Pattern → Pattern (these patterns have never co-occurred — useful negative signal) |

`Extends` is directional: `InteractiveLoop` extends `UnboundedLoop` (adds StdinInput). `GuessingGame` extends `InteractiveLoop` (adds RandomSource).

### Output schema

```json
{
  "generatedAt": "...",
  "patternNodes": [
    {
      "id": "uuid",
      "type": "BoundedLoop",
      "attestedBy": ["FizzBuzz", "Fibonacci", "BubbleSort"],
      "attestationCount": 8,
      "representativeSpecText": "loop:\n  from: 1\n  to: 100"
    }
  ],
  "patternEdges": [
    {
      "from": "uuid-InteractiveLoop",
      "to": "uuid-UnboundedLoop",
      "type": "Extends"
    }
  ]
}
```

### Implementation

Add `GraphMemoryBuilder` to `Specc/Passes/Repository/`:

```csharp
public static class GraphMemoryBuilder
{
    public static Task BuildAsync(string repositoryPath);
}
```

`BuildAsync` scans `index.json`, extracts patterns from each `SpecText`, counts co-occurrences, and writes `repository-graph.json`. Call it from `RepositoryPersistPass` after a successful persist — or as a standalone CLI command (`specc graph-memory`).

---

## Phase B — Graph-aware retrieval

### Goal

When `MarkdownSpecPass` retrieves priors, the retrieval pass can query the pattern graph to find the most relevant structural templates — not just by tag overlap but by pattern match.

### Design

After tag-based retrieval, score each candidate by how many of its attested patterns overlap with the query's detected construct families. A compilation that uses three of the query's five detected patterns ranks above one that uses one.

This is additive: the existing tag-overlap score is still primary. Pattern overlap is a secondary tiebreaker within the verified tier, sitting between assertion count and spec text length.

```csharp
// In FindPriorsByTagsAsync:
.ThenByDescending(x => PatternOverlapScore(x, queryPatterns, graphMemory))
.ThenByDescending(x => x.Verified ? x.AssertionCount : -1)
.ThenByDescending(x => x.SpecText.Length)
```

---

## Phase C — Failure memory

### Goal

The repository currently has no memory of what fails. A pattern that consistently breaks extraction is invisible — it will be attempted again on every new program that triggers it, with no signal from prior attempts.

### Design

Add a `FailureNode` to the pattern graph. When `AcceptanceVerificationPass` fails, extract the spec that was attempted and identify which patterns it contained. Increment a failure count on those pattern nodes.

At retrieval time, patterns with high failure counts can be flagged in the extraction prompt: "prior compilations using this construct combination have failed; prefer the simpler alternative." This is a soft signal, not a hard block.

### Schema addition

```json
{
  "type": "BoundedLoop",
  "attestationCount": 8,
  "failureCount": 2,
  "failureRate": 0.2
}
```

A `failureRate` above a threshold (e.g. 0.4) surfaces as a warning in the extraction prompt.

---

## Phase D — Abstract pattern injection

### Goal

Replace (or supplement) raw spec text injection in `MarkdownSpecPass` with abstract pattern nodes from the graph. Instead of injecting "here is GuessingGame's full spec", inject "here is the InteractiveLoop pattern, attested by 3 compilations, representative spec: ...".

This is the key compounding behavior: as the repository grows, the injected context improves not because there are more individual specs but because the patterns are better attested. A hundred compilations of interactive loops collapse to one well-attested pattern node.

### Open question

Does abstract pattern injection produce better extraction than instance injection? The answer is not obvious. A concrete spec gives the model a complete working example to copy. An abstract pattern gives it structural rules to follow. The right answer may be both: inject the abstract pattern description followed by its best-attested concrete instance.

---

## Relationship to other specs

- **Spec 41 (MLP training)**: the pattern graph is a natural supervision signal for the MLP. `Composes` and `Extends` edges between pattern nodes provide a relational structure that contrastive loss could train toward — nodes that participate in the same pattern should be pulled together, nodes in conflicting patterns pushed apart.
- **Spec 44 (repository health)**: Phase A of this spec is downstream of Spec 44 — eviction should run before the pattern graph is rebuilt, so stale entries do not contribute to pattern counts.
- **Spec 03 (graph repository)**: this spec extends the repository from a flat list to a two-level structure (episodic index + semantic graph). The episodic layer is unchanged.

---

## Implementation order

- Phase A: `GraphMemoryBuilder` (offline, additive, no pipeline risk) — implement first, validate pattern extraction against known examples.
- Phase B: pattern-aware retrieval — implement after Phase A, measure extraction quality before/after.
- Phase C: failure memory — implement after the repository has accumulated enough failed compilations to be meaningful (minimum ~5 failures).
- Phase D: abstract pattern injection — research question; implement as an A/B flag against the current instance injection.

## Tests

- `GraphMemoryBuilder_ExtractsBoundedLoopFromFizzBuzz`: given a repository containing FizzBuzz, the output graph contains a `BoundedLoop` pattern node attested by FizzBuzz.
- `GraphMemoryBuilder_ExtractsInteractiveLoopFromGuessingGame`: `InteractiveLoop` pattern is present and has an `Extends` edge to `UnboundedLoop`.
- `GraphMemoryBuilder_PatternsRequireMinimumAttestation`: a pattern appearing in only one compilation is not emitted.
- `PatternOverlapScore_RanksHigherOnMorePatternMatches`: two candidates with the same tag score but different pattern overlap; higher overlap ranks first.
