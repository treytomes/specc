# Spec 06 — Semantic Validation Pass

**Status:** Ready to implement  
**Scope:** New `Passes/SemanticValidationPass.cs`

## Motivation

Every pass can have invariants. Semantic graph: exactly one entry node, every node reachable, no dangling edges. CFG: every block terminates, no unreachable blocks. Stack IR: every branch resolves to a label, sequence ends with `Ret`. If any pass produces something invalid, the compiler rejects it with a descriptive error — exactly like a normal compiler.

We have ad-hoc validation scattered across passes. This spec moves invariant checking into its own named pass that runs after `SemanticGraphPass` and produces a structured validation report as an artifact.

## Invariants to Check

### Semantic Graph

| Invariant | Check |
|-----------|-------|
| Exactly one `ProgramNode` | `nodes.OfType<ProgramNode>().Count() == 1` |
| Every non-Program, non-Assertion node reachable from ProgramNode | BFS from ProgramNode via non-Asserts edges |
| No dangling edge endpoints | every `edge.From` and `edge.To` exists in `nodes` |
| Every `BranchNode` has at least one outgoing `TrueBranch` edge | |
| Every `ModuloNode` is referenced by at least one `DependsOn` edge | |

`AssertionNode`s (added by `AcceptanceCriteriaPass`) are reachable via `EdgeType.Asserts` and should not be included in the "all nodes reachable" check.

### CFG

| Invariant | Check |
|-----------|-------|
| At least one block | |
| All block labels unique | |
| All successor references resolve to a known label | |
| Every non-exit block has at least one instruction | |
| Exactly one block with no successors (exit) | |

### Stack IR

| Invariant | Check |
|-----------|-------|
| Every `Label` pseudo-op has a unique operand | |
| Every `Brfalse` / `Br` operand matches a `Label` in the sequence | |
| Sequence ends with `Ret` | |

## Output Artifact

`08-validation.json` — written by `ArtifactWriter`:

```json
{
  "passed": true,
  "checks": [
    { "name": "single program node",     "passed": true },
    { "name": "all nodes reachable",     "passed": true },
    { "name": "no dangling edges",       "passed": true },
    { "name": "cfg blocks terminate",    "passed": true },
    { "name": "stack ir labels resolve", "passed": true }
  ]
}
```

If any check fails, the pass throws `CompilationException` with the failing check name and details. The artifact is written even on failure so it can be opened for debugging.

## Pipeline Placement

```
ParseSpecPass
SemanticGraphPass
GraphVisualizationPass
AcceptanceCriteriaPass
EmbeddingPass
SemanticNormalizationPass
CfgPass
StackIrPass
SemanticValidationPass    ← validates graph, CFG, and stack IR in one pass
MsilGenerationPass
AssemblyEmitPass
AcceptanceVerificationPass
```

The pass runs after `StackIrPass` so it can validate all three layers in a single execution with context already populated. If earlier-stage validation is needed independently, make the pass accept a `ValidationScope` flags enum (`Graph`, `Cfg`, `StackIr`) and register it at multiple points with different configurations.
