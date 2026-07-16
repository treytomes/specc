# Spec 07 — Graph Visualization

**Status:** Ready to implement  
**Scope:** New `Passes/GraphVisualizationPass.cs`, new artifact `02b-semantic-graph.mmd`

## Goal

After the semantic graph is built, emit two visualization artifacts alongside the JSON:

- `02b-semantic-graph.mmd` — Mermaid flowchart
- `02c-semantic-graph.svg` — Standalone SVG (no external renderer required)

Both are produced deterministically from the in-memory `SemanticGraph` — no LLM, no network call.

## Mermaid Output

```
flowchart TD
  Program_FizzBuzz["Program: FizzBuzz"]
  Loop_1_100["Loop: 1..100"]
  Branch_divisible_by_15["Branch: divisible_by_15"]
  Modulo_15["Modulo: 15"]
  Print_FizzBuzz["Print: FizzBuzz"]
  ...

  Program_FizzBuzz -- Contains --> Loop_1_100
  Program_FizzBuzz -- Contains --> Branch_divisible_by_15
  Branch_divisible_by_15 -- DependsOn --> Modulo_15
  Branch_divisible_by_15 -- TrueBranch --> Print_FizzBuzz
  ...
```

Node IDs in the diagram are derived from `node.Label` with spaces and colons replaced by underscores.

## SVG Output

Built by `GraphVisualizationPass` using a simple layered layout algorithm:

1. **Rank nodes** — BFS from the `ProgramNode`, assigning each node a depth.
2. **Position nodes** — each rank gets a horizontal row; nodes within a rank are spaced evenly.
3. **Emit SVG** — rectangles with labels, lines (or curves) for edges, arrowheads.

No external SVG library required — write raw SVG strings. The output should be self-contained (no external references) and render correctly in any browser or IDE preview.

### Node colour by kind

| Kind | Fill |
|------|------|
| ProgramNode | `#4a90d9` |
| LoopNode | `#7b68ee` |
| BranchNode | `#f5a623` |
| ModuloNode | `#e8e8e8` |
| PrintNode | `#7ed321` |
| VariableNode | `#d0d0d0` |

### Edge style by type

| EdgeType | Style |
|----------|-------|
| Contains | solid |
| TrueBranch | solid, green |
| FalseBranch | dashed, red |
| DependsOn | dotted, grey |

## Pipeline Placement

After `SemanticGraphPass`:

```
ParseSpecPass
SemanticGraphPass
GraphVisualizationPass   ← new
EmbeddingPass
...
```

`ArtifactFile` → `"02b-semantic-graph.mmd"` (the SVG is written alongside it but not tracked as the primary artifact).

## Acceptance Criterion

Running `dotnet run` produces `02b-semantic-graph.mmd` and `02c-semantic-graph.svg` in the artifacts directory. Opening the SVG in a browser shows the FizzBuzz graph with labelled nodes and directed edges.
