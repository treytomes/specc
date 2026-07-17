# Spec 30 — Fibonacci LLM Extraction

## Status

Completed.

## Problem

The Fibonacci algorithm requires copying one variable into another:

```
tmp = a
a = a + b
b = tmp
```

Before this spec, the `assign:` format had no copy op. The only way to express `tmp = a` was `add {a} 0`. ministral-3b conflated this unnatural idiom with the actual addition step, generating `tmp = a + b` instead of `tmp = a`. It also added spurious conditional branches (`n == 1`, `n == 2`) and an explicit `n` increment inside `assign:` blocks, causing double-increment of the loop counter.

## Solution

### 1. Added `op: copy` to the `assign:` format

`copy` has only a `left:` operand — no `right:`:

```
assign:
  target: tmp
  op: copy
  left: {a}
```

`SemanticGraphPass.BuildAssignNodes` flushes a copy assign when `target` and `left` are present, without requiring `right`. `AssignNode.Right` is now nullable.

### 2. Updated `StackIrPass`

New pattern before the existing `mul|add|sub` pattern:

```
"assign {target} copy {left}"  →  load left, StlocS target
```

No arithmetic opcode — just a load and store.

### 3. Updated `MarkdownSpecPass` system prompt

- Added `copy` to the list of valid ops with an explicit example.
- Added rule 7: "Do NOT add an assign: block that increments the loop counter."

### 4. Rewrote `Fibonacci.md`

Removed the phrase "Fibonacci numbers" from the algorithm description so the model does not need to recall the sequence from the name. Described the steps purely procedurally: explicit initial values, explicit step order, "Copy the value of `a` into `tmp`." See Spec 25 for the final Markdown content.

### 5. Updated `SemanticNormalizationPass`

`ArithmeticNode` and `AssignNode` are now skipped by the similarity gate (alongside `AssertionNode`). Both are exact-typed from the parsed spec and do not benefit from vector-based reclassification. Added both to the reference corpus and `KindOf`/`NormalizeLabel`/`Reclassify` for completeness.

## Result

`scripts/run.sh examples/Fibonacci/Fibonacci.md` → 10/10 assertions pass. ministral-3b correctly emits:

```
assign:
  target: tmp
  op: copy
  left: {a}

assign:
  target: a
  op: add
  left: {a}
  right: {b}

assign:
  target: b
  op: copy
  left: {tmp}
```

The spurious `is_odd` branch it also generates is silently dropped by `CfgPass` (modulo branches are ignored when assign blocks are present).
