# Spec 15 — Array and Nested Loop Node Types

**Status:** Ready to implement  
**Scope:** `IronLlm/Graph/Nodes.cs`, `SemanticGraphPass.cs`, `GraphVisualizationPass.cs`, `EmbeddingPass.cs`, `SemanticNormalizationPass.cs`, `SemanticValidationPass.cs`

## Motivation

The current IR supports one pattern: a flat loop over a scalar variable with modulo branches. BubbleSort requires nodes the graph layer cannot yet express: an array, element access by index, a swap operation, and a loop whose upper bound depends on another loop's counter. This spec adds those node types and extends every pass that touches the graph — but stops at CFG lowering. The new nodes will appear in the semantic graph, be visualized, and receive embeddings; they will not yet be compiled to runnable code.

## New Node Types

Add to `IronLlm/Graph/Nodes.cs` and register the `[JsonDerivedType]` annotations on `Node`:

```csharp
// An array with a fixed size and optional literal initializer values.
public record ArrayNode(Guid Id, string Label, string Name, string ElementType, int Size, int[]? Values = null)
    : Node(Id, Label);

// Access to array element at a given index expression.
// IndexExpr is either a variable name ("j") or a simple expression ("j+1", "8-i").
public record IndexNode(Guid Id, string Label, string ArrayName, string IndexExpr)
    : Node(Id, Label);

// Swap two array elements. FromExpr and ToExpr are index expressions.
public record SwapNode(Guid Id, string Label, string ArrayName, string FromExpr, string ToExpr)
    : Node(Id, Label);

// A loop whose upper bound is a runtime expression rather than a literal.
// BoundExpr is a simple expression like "8 - i" referencing an outer loop variable.
public record NestedLoopNode(Guid Id, string Label, string Variable, int From, string BoundExpr)
    : Node(Id, Label);
```

## SemanticGraphPass

The `.spec` format does not yet have syntax for these constructs — they are graph-only types introduced by `MarkdownSpecPass` when the LLM extracts a BubbleSort description. `SemanticGraphPass` (the deterministic parser for `.spec` files) does not need changes; the new nodes are only reachable via the Markdown LLM path.

However, `SemanticGraphPass.LoadFromArtifactAsync` and the JSON deserializer must handle the new discriminators. Since `[JsonPolymorphic]` is on `Node`, adding the `[JsonDerivedType]` annotations is sufficient — no other change needed.

## GraphVisualizationPass

Add color and shape assignments for the four new node types:

| Node | Color |
|------|-------|
| `ArrayNode`      | `#d4a843` (amber) |
| `IndexNode`      | `#c4a4e0` (lavender) |
| `SwapNode`       | `#e08080` (salmon) |
| `NestedLoopNode` | `#7ab8c4` (teal, distinct from LoopNode's blue) |

Mermaid label format:
- `ArrayNode`: `Array:{Name}[{Size}]`
- `IndexNode`: `Index:{ArrayName}[{IndexExpr}]`
- `SwapNode`: `Swap:{ArrayName}[{FromExpr}↔{ToExpr}]`
- `NestedLoopNode`: `NestedLoop:{Variable}&lt;{BoundExpr}`

## EmbeddingPass

No changes required — `EmbeddingPass` embeds every non-`AssertionNode` by its label. The new node types will receive embeddings automatically via the existing loop.

## SemanticNormalizationPass

Add four entries to `ReferenceCorpus`:

```csharp
("Array",      "An array of integer values with a fixed size."),
("Index",      "Access to an array element by index position."),
("Swap",       "Swap two elements in an array."),
("NestedLoop", "An inner loop whose upper bound depends on an outer loop variable."),
```

Add corresponding cases to `KindOf`, `Reclassify`, and `NormalizeLabel`:
- `NormalizeLabel` for `ArrayNode`: `"Array:{Name}[{Size}]"`
- `NormalizeLabel` for `IndexNode`: `"Index:{ArrayName}[{IndexExpr}]"`
- `NormalizeLabel` for `SwapNode`: `"Swap:{ArrayName}[{FromExpr}↔{ToExpr}]"`
- `NormalizeLabel` for `NestedLoopNode`: `"NestedLoop:{Variable}<{BoundExpr}"`

## SemanticValidationPass

Add graph-layer invariants for the new types:
- Every `IndexNode` references an `ArrayNode` that exists in the graph (by `ArrayName` matching `ArrayNode.Name`).
- Every `SwapNode` references an `ArrayNode` that exists in the graph.
- Every `NestedLoopNode` references a `VariableNode` or `LoopNode` whose variable name appears in `BoundExpr` (outer loop variable is in scope).

## Tests

- Deserializing a graph JSON containing the four new `kind` discriminators round-trips correctly.
- `GraphVisualizationPass.BuildMermaid` includes correct labels for each new node type.
- `GraphVisualizationPass.BuildSvg` assigns distinct colors for each new node type.
- `SemanticNormalizationPass.KindOf` returns the correct kind for each new node.
- `SemanticNormalizationPass.NormalizeLabel` returns the correct label form for each new node.
- `SemanticValidationPass` passes for a valid graph containing the new types.
- `SemanticValidationPass` throws when an `IndexNode` references a non-existent array.
- `SemanticValidationPass` throws when a `SwapNode` references a non-existent array.
