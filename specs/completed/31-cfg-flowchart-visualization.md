# Spec 31 — CFG Flowchart Visualization

## Status

Completed.

## Motivation

The existing `GraphVisualizationPass` (pass 02b) renders the **semantic graph** — a containment diagram where the program node owns its constituent nodes via `Contains` edges. This is useful for understanding what the compiler knows about a program, but it does not look like a program flow document. Nodes appear clustered by structural membership, not by execution order.

The **CFG** (`04-cfg.json`) already contains everything needed for a human-readable flowchart: blocks are execution-ordered, instruction strings are human-readable, and successor labels encode branching exactly. A new visualization pass that reads `context.CfgBlocks` can produce the flowchart a developer would draw by hand.

The two diagrams serve different audiences:
- Semantic graph (02b): compiler-legible structure — what the graph knows
- CFG flowchart (new): human-legible execution flow — what the program does

## New pass: `CfgVisualizationPass`

**Name:** `04b-CfgVisualization`  
**Artifact files:** `04b-cfg-flowchart.mmd` + `04c-cfg-flowchart.svg`  
**Runs after:** `CfgPass` (04)  
**Input:** `context.CfgBlocks`

### Mermaid output

Each `CfgBlock` becomes a node. Instructions are listed inside the node label. Successors become edges, labelled with the branch direction when both are present.

Block shape conventions:
- `entry` / `exit` — stadium/pill shape (`([label])`)
- Blocks whose sole instruction is an `if …` test — diamond (`{label}`)
- All other blocks — rectangle (`[label]`)

Edge label conventions:
- When a block has both `SuccessorTrue` and `SuccessorFalse`: true edge labelled `yes`, false edge labelled `no`
- When a block has only `SuccessorTrue`: unlabelled edge
- Back-edges (target block has lower index than source) are included — Mermaid renders them as curved arrows, visually distinguishing loops

Node label format: block label on the first line, then each instruction on its own line, separated by `<br/>` in Mermaid syntax.

Example output for Fibonacci:

```
flowchart TD
  entry(["entry<br/>a = 1<br/>b = 0<br/>n = 1"])
  loop_test{"loop_test<br/>if n &gt; 10 goto exit"}
  body["body<br/>print a<br/>assign tmp copy {a}<br/>assign a add {a} {b}<br/>assign b copy {tmp}"]
  loop_inc["loop_inc<br/>n = n + 1"]
  exit(["exit"])

  entry --> loop_test
  loop_test -->|yes| body
  loop_test -->|no| exit
  body --> loop_inc
  loop_inc --> loop_test
```

### SVG output

Same layout engine as `GraphVisualizationPass.BuildSvg` (BFS rank assignment, rows positioned top-to-bottom), adapted for CFG blocks:

- BFS traversal starts at the `entry` block; uses `SuccessorTrue` for rank assignment (ignores `SuccessorFalse` to keep the dominant path vertical)
- Back-edges (loop back-jumps) are drawn as curved arcs to the left of the diagram rather than straight lines, so they don't cross forward edges
- Block rectangles are wider than the semantic-graph nodes to fit multi-line instruction text
- Instructions rendered as additional `<text>` lines inside each block rect
- Branch edges: `SuccessorTrue` in green, `SuccessorFalse` in amber (matches semantic graph convention, distinguishable without colour by label)
- Back-edge arcs: grey dashed

Block sizing: variable height based on instruction count. Base height 36px per line with 4px padding top and bottom.

Back-edge detection: a target block whose topological index (position in `context.CfgBlocks`) is less than or equal to the source block is a back-edge.

## Pipeline placement

`CfgVisualizationPass` runs immediately after `CfgPass`:

```
CfgPass (04) → CfgVisualizationPass (04b) → StackIrPass (05) → …
```

It is registered in `Program.cs` in the pass list between `CfgPass` and `StackIrPass`.

## `LoadFromArtifactAsync`

The `.mmd` file is the artifact. `LoadFromArtifactAsync` is a no-op (same pattern as `GraphVisualizationPass` — the SVG is regenerated alongside the Mermaid; neither artifact affects downstream context).

## Acceptance criteria

1. `scripts/run.sh examples/Fibonacci/Fibonacci.md` produces `04b-cfg-flowchart.mmd` and `04c-cfg-flowchart.svg` in the artifacts directory.
2. The Mermaid file is valid: opening it in a Mermaid renderer (e.g. mermaid.live) renders without errors.
3. The flowchart contains exactly one node per CFG block and one edge per successor relationship.
4. `entry` and `exit` nodes use the stadium shape; condition-check blocks use the diamond shape.
5. Back-edges (loop jumps) are present and labelled correctly — `loop_inc → loop_test` appears in the Fibonacci diagram.
6. The SVG renders all blocks and edges without overlap for FizzBuzz (10 blocks), Fibonacci (5 blocks), and SelectionSort (16 blocks).
7. All existing tests pass. No existing artifacts are changed.

## Not in scope

- Interactive SVG (hover, click-to-expand)
- Instruction-level syntax highlighting
- Subgraph grouping (e.g. grouping inner/outer loop blocks visually)
- Replacing or deprecating the existing semantic graph visualization (02b/02c remain as-is)
