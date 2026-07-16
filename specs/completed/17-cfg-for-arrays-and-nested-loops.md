# Spec 17 — CFG Lowering for Arrays and Nested Loops

**Status:** Ready to implement (after Spec 16)  
**Scope:** `CfgPass.cs`, `CfgBlock.cs` (no changes), new CFG patterns for array init, nested loops, index reads/writes, swap

## Motivation

`CfgPass` currently handles exactly one shape: flat loop, modulo branches, scalar variable. BubbleSort introduces three new lowering problems — array initialization, a nested loop with a dynamic upper bound, and conditional swap — each requiring new block patterns in the CFG.

## New CFG Patterns

### Array Initialization

```
entry:
  arr[0] = 64
  arr[1] = 34
  ...
  arr[9] = 3
  i = 0
  → outer_loop_test
```

Array element assignments use the instruction form `arr[{idx}] = {val}`. All 10 assignments are emitted in the `entry` block before the loop counter init.

### Outer Loop

```
outer_loop_test:
  if i > 8 goto exit
  → inner_loop_init

inner_loop_init:
  j = 0
  → inner_loop_test

inner_loop_test:
  if j > (8 - i) goto outer_loop_inc
  → compare

compare:
  if arr[j] > arr[j+1]
  → swap | → inner_loop_inc

swap:
  swap arr[j] arr[j+1]
  → inner_loop_inc

inner_loop_inc:
  j = j + 1
  → inner_loop_test

outer_loop_inc:
  i = i + 1
  → outer_loop_test
```

### Print Loop (post-sort)

```
print_init:
  k = 0
  → print_loop_test

print_loop_test:
  if k > 9 goto exit
  → print_element

print_element:
  print arr[k]
  → print_inc

print_inc:
  k = k + 1
  → print_loop_test

exit:
```

## New Instruction Forms

`CfgBlock.Instructions` uses string instructions. New forms required:

| Pattern | Example |
|---------|---------|
| Array element assignment | `arr[0] = 64` |
| Dynamic loop upper bound | `if j > (8 - i) goto outer_loop_inc` |
| Array comparison | `if arr[j] > arr[j+1]` |
| Array swap | `swap arr[j] arr[j+1]` |
| Array print | `print arr[k]` |

## CfgPass Changes

`CfgPass.ExecuteAsync` currently handles only `LoopNode`/`BranchNode`/`ModuloNode`/`VariableNode`. Extend with a detection branch:

- If the graph contains an `ArrayNode`, route to `LowerArrayProgram(graph)` instead of the existing `LowerFlatLoop(graph)`.
- `LowerFlatLoop` becomes the existing logic, extracted into a named method.
- `LowerArrayProgram` implements the block patterns above, deriving array name, size, values, outer variable, inner variable, and bound expression from the graph nodes.

`Validate` already checks unique labels and resolved successors — no changes needed there.

## Tests

- `LowerArrayProgram` produces the expected block sequence for a BubbleSort graph.
- All block labels are unique.
- All successor references resolve.
- The compare block has two successors (swap and inner_loop_inc).
- The exit block has no successors.
- `SemanticValidationPass` passes on the resulting CFG.
