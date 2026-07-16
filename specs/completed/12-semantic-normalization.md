# Spec 12 — Semantic Normalization Pass

**Status:** Ready to implement  
**Scope:** New `Passes/SemanticNormalizationPass.cs`; no changes to existing passes

## Problem

The compiler's intermediate passes are brittle to synonym variation in LLM-generated content. Today the pipeline is insulated from this because the CFG is built deterministically from the typed semantic graph — but two upstream paths can introduce phrasing variation that the graph parser doesn't see:

1. **Spec 10 (Markdown ingestion):** The LLM extracts a `.spec` file from prose. It might write `output: "Fizz"` instead of `true_output: "Fizz"`, or use `repeat:` instead of `loop:`. The structured parser in `ParseSpecPass` will either fail or silently drop fields.

2. **Future LLM passes that write directly to the graph:** Any pass that asks an LLM to annotate or augment graph nodes will produce labels and templates in natural language. "Emit the string Fizz", "write Fizz to stdout", and `print "Fizz"` are semantically identical but pattern-match differently.

The embedding pass (Spec 03) already captures semantic meaning as a vector per node — it just doesn't do anything with it yet. Those vectors are the right tool for this problem.

## Core idea

Build a small **reference corpus** of canonical descriptions — one per node type — and embed them at pass startup. For each node in the live graph, compute cosine similarity between its embedding and all reference embeddings. If the closest canonical is above a similarity threshold, normalize the node's label (and type, if it was misclassified) to match the canonical form.

This is semantic type-checking: instead of asking "does this label string match a pattern?", ask "is this node's meaning close enough to a known kind of node?"

## Pipeline position

```
EmbeddingPass       ← 03-embeddings.json       node vectors already computed
SemanticNormalizationPass  ← NEW, 03b-normalized-graph.json
CfgPass             ← reads normalized graph, not raw graph
```

`CompilationContext` already holds both `SemanticGraph` and `Embeddings`. The normalization pass reads both and writes a modified graph back to `context.SemanticGraph`. It does not change the embeddings.

## Reference corpus

One canonical description per node type, written to match what `EmbeddingPass.Describe()` generates for a well-formed node of that type:

| Kind | Canonical description |
|------|-----------------------|
| `ProgramNode` | "A program with a name." |
| `LoopNode` | "Iterates integers sequentially over a range." |
| `BranchNode` | "A conditional branch that tests a named condition." |
| `PrintNode` | "Outputs a value or string to the console." |
| `ModuloNode` | "Computes the remainder after integer division." |
| `VariableNode` | "A named variable that holds an integer value." |
| `ConstantNode` | "An integer constant literal." |
| `ComparisonNode` | "Compares two values and produces a boolean result." |

These are embedded once at pass initialization using the same Ollama model (`mxbai-embed-large`). The reference vectors are not persisted — they're cheap to recompute (8 calls, one per type).

## Normalization algorithm

```
for each node in SemanticGraph.Nodes:
    embedding ← Embeddings[node.Id]
    nearest   ← argmax over canonicals of CosineSimilarity(embedding, canonical.vector)
    if nearest.similarity ≥ THRESHOLD:
        if node.Kind != nearest.Kind:
            log warning: "Node '{node.Label}' reclassified from {node.Kind} to {nearest.Kind}"
            node ← Reclassify(node, nearest.Kind)
        node.Label ← NormalizeLabel(node, nearest.Kind)
    else:
        throw CompilationException:
            $"Node '{node.Label}' (similarity {nearest.similarity:F2}) does not match any known node type"
```

**Threshold:** Start at `0.80` cosine similarity. The embedding model places synonyms within ~0.85–0.95 of each other; unrelated concepts fall below 0.60. A threshold of 0.80 leaves a comfortable margin.

**`Reclassify`:** Constructs a new node of the correct type, carrying over whatever properties can be inferred from the label. For example, a node labelled "output the string Buzz" whose embedding is closest to `PrintNode` would be reclassified as `PrintNode` with `Template = "Buzz"`.

**`NormalizeLabel`:** Rewrites the node's `Label` to the canonical form `"Kind:value"` (e.g., `"Print:Buzz"`, `"Loop:1..100"`) so downstream passes that key on labels have a stable contract.

## What this does NOT do

- It does not re-embed nodes. Embeddings are read-only inputs.
- It does not rewrite `.spec` content. Normalization happens on the typed graph, after parsing.
- It does not handle structural errors (a spec with no `loop:` block). That is Spec 06's job (semantic validation).
- It does not normalize the *values* inside nodes (e.g., it won't convert `"one hundred"` to `100` inside a `LoopNode`). That is a separate parsing concern.

## Artifact

`03b-normalized-graph.json` — same schema as `02-semantic-graph.json`. The incremental pipeline skips this pass if the file exists; deleting it forces re-normalization without re-embedding.

## `CompilationContext` change

Add a single flag:

```csharp
public bool GraphNormalized { get; set; } = false;
```

`CfgPass` (and any future pass that reads the graph) can assert `context.GraphNormalized` as a precondition when Spec 10 or any LLM-driven graph annotation is in use.

## Failure modes

| Condition | Outcome |
|-----------|---------|
| Node similarity < threshold | `CompilationException` naming the node and its score |
| Reference corpus embedding fails (Ollama down) | Propagates as `HttpRequestException` — same as `EmbeddingPass` |
| Node reclassification loses required properties | `CompilationException` describing what could not be inferred |

## Relationship to other specs

- **Spec 06 (Semantic Validation):** Validation checks structural invariants *after* normalization. A normalized graph is a precondition for reliable validation.
- **Spec 10 (Markdown Ingestion):** The primary producer of unnormalized nodes. Normalization is what makes Spec 10 viable without requiring the LLM to produce perfectly structured `.spec` output.
- **Spec 03 (Graph Repository):** Retrieved prior graphs were normalized when originally compiled; a normalization pass on the query graph enables meaningful structural comparison.

## Test strategy

The normalization pass calls Ollama to embed the reference corpus. For unit tests, inject a mock `IEmbeddingGenerator` that returns known vectors:

- Assign each canonical a unit vector in a distinct dimension (8 dimensions, one-hot).
- Assign each test node's embedding to the same vector as its expected canonical.
- Test that similarity is computed correctly and normalization produces the expected label.

This makes the tests hermetic and fast, decoupled from real model output.

## Example

Given a node emitted by a future LLM pass:

```json
{ "kind": "Print", "label": "write 'Fizz' to the output stream", "template": "write 'Fizz' to the output stream" }
```

After normalization:

```json
{ "kind": "Print", "label": "Print:Fizz", "template": "Fizz" }
```

The `CfgPass` then produces `print "Fizz"` in the correct block, exactly as if the spec had been written by hand.
