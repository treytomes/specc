# Spec 06 — Semantic Validation Pass

**Status:** Ready to implement  
**Scope:** New `Passes/SemanticValidationPass.cs`

## Motivation

From the conversation:

> Every pass can have invariants. Semantic graph: exactly one entry node. Every node reachable. No dangling edges. CFG: every block terminates. No unreachable blocks. If an LLM produces something invalid, the compiler rejects it. Exactly like a normal compiler.

We have ad-hoc validation in `CfgPass.Validate`. This spec moves invariant checking into its own named pass that runs after `SemanticGraphPass` and produces a structured validation report as an artifact.

## Invariants to Check

### Semantic Graph

| Invariant | Check |
|-----------|-------|
| Exactly one `ProgramNode` | `nodes.OfType<ProgramNode>().Count() == 1` |
| Every non-Program node reachable from Program | BFS/DFS from ProgramNode via edges |
| No dangling edge endpoints | every `edge.From` and `edge.To` exists in `nodes` |
| Every `BranchNode` has at least one outgoing `TrueBranch` edge | |
| Every `ModuloNode` is referenced by at least one `DependsOn` edge | |

### CFG (moved from CfgPass)

| Invariant | Check |
|-----------|-------|
| At least one block | |
| All block labels unique | |
| All successor references resolve | |
| Every non-exit block has at least one instruction | |
| Exactly one block with no successors (exit) | |

### Stack IR

| Invariant | Check |
|-----------|-------|
| Every `Label` pseudo-op has a unique operand | |
| Every `Brfalse` / `Br` operand matches a `Label` in the same sequence | |
| Sequence ends with `Ret` | |

## Output Artifact

`07-validation.json` — written by `ArtifactWriter` if present in context:

```json
{
  "passed": true,
  "checks": [
    { "name": "single program node",    "passed": true  },
    { "name": "all nodes reachable",    "passed": true  },
    { "name": "no dangling edges",      "passed": true  },
    { "name": "cfg blocks terminate",   "passed": true  },
    { "name": "stack ir labels resolve","passed": true  }
  ]
}
```

If any check fails, the pass throws `CompilationException` with the failing check name and details. The artifact is written even on failure (for debugging).

## Placement in Pipeline

```
ParseSpecPass
SemanticGraphPass
SemanticValidationPass   ← new, runs after graph is built
EmbeddingPass
CfgPass
SemanticValidationPass   ← consider running again after CFG to re-check
StackIrPass
MsilGenerationPass
```

Alternatively, make the pass accept a `ValidationStage` enum (`AfterGraph`, `AfterCfg`, `AfterStackIr`) and add it three times in the pipeline with different configurations.
