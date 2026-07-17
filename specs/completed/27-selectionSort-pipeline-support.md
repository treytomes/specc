# Spec 27 — SelectionSort Full Pipeline Support

**Status:** Incomplete  
**Scope:** `SemanticGraphPass.cs`, `CfgPass.cs`; `MarkdownSpecPass.cs` already fixed

## What's Already Done

- `CfgPass.LowerSelectionSort` is implemented and tested via `BuildSelectionSortGraph` (graph built directly, bypassing the LLM path). The 8-element sort is correct: 274/274 tests pass.
- `MarkdownSpecPass.ValidateExtracted` now catches `CompilationException` in addition to `FormatException`, so the "divisor: 0 in branch" crash no longer prevents the LLM from writing the extracted `.spec` to disk.

## Remaining Blockers

### 1 — `SemanticGraphPass` does not parse array values from the spec

`BuildVariableNode` in `SemanticGraphPass` emits an `ArrayNode` when it sees `type: array[int]`, but it does not parse the `initial_value:` line. `ArrayNode.Values` is always null when the graph is built via the spec path. `CfgPass.LowerSelectionSort` falls back to `Enumerable.Range(0, arraySize)` when `Values` is null, producing a trivially sorted array `[0,1,...,7]` — wrong output.

**Fix**: parse `initial_value: [v0, v1, ..., vN]` in `BuildVariableNode` and populate `ArrayNode.Values`. The format is the bracketed integer list the LLM already produces (confirmed in BubbleSort's `00-extracted.spec`).

```csharp
// In BuildVariableNode, alongside name/type parsing:
if (line.StartsWith("initial_value:"))
{
    initialValue = line["initial_value:".Length..].Trim();
    continue;
}

// In EmitVariable, when building ArrayNode:
int[]? values = null;
if (initialValueRaw != null)
{
    var nums = initialValueRaw.Trim('[', ']')
        .Split(',', StringSplitOptions.TrimEntries)
        .Select(t => int.TryParse(t, out var v) ? v : 0)
        .ToArray();
    if (nums.Length == size) values = nums;
}
var arr = new ArrayNode(Guid.NewGuid(), $"Array:{name}[{size}]", name, "int", size, values);
```

### 2 — `CfgPass.LowerBubbleSort` hardcodes fallback values

`LowerBubbleSort` uses `arr.Values ?? new[] { 64, 34, 25, 12, 22, 11, 90, 45, 78, 3 }`. Once Blocker 1 is fixed the fallback is dead code, but it should be changed to `arr.Values ?? throw` to make the contract explicit. This is a cleanup, not a correctness fix.

### 3 — `SemanticGraphPass` does not parse `SwapNode` or `NestedLoopNode` from spec text

`CfgPass.LowerSelectionSort` dispatches on the presence of a `VariableNode` named `min_index`. If the LLM produces a spec with `variable: name: min_index`, `BuildVariableNode` will emit a `VariableNode(min_index)` and the correct lowering path will be taken. This is already implicit in the current dispatch logic and requires no additional parser changes.

If the LLM does not include `min_index` as a variable, `LowerBubbleSort` will be called instead, producing wrong output. The acceptance check will catch this. The remedy is prompt improvement, not parser changes.

## Acceptance Criteria

1. Delete `examples/SelectionSort/artifacts/` and re-run `scripts/run.sh examples/SelectionSort`. The pipeline completes without error.
2. The compiled binary produces exactly 8 lines matching the `## Expected Output` in `SelectionSort.md`.
3. `AcceptanceVerificationPass` reports 8/8 assertions passing.
4. BubbleSort still passes (10/10).
5. All 274+ unit tests pass.

## Not In Scope

- Changes to the `.spec` format or LLM prompt for array programs.
- Array value handling for `LowerSelectionSort`'s fallback (already uses `Enumerable.Range` as placeholder).
