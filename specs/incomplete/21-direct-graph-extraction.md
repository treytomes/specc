# Spec 21 — Direct Graph Extraction from Markdown

**Status:** Deferred — blocked on model capacity (ministral-3b too slow and unreliable for graph-sized output; requires a larger model)
**Scope:** `MarkdownSpecPass.cs`; `ParseSpecPass` and `SemanticGraphPass` skip when graph is already set

## Implementation Notes

### Attempt 1 (2026-07-16) — plain JSON, no schema constraint
Blocked by two issues:
1. **Speed**: ~600 JSON output tokens vs ~100 `.spec` tokens → ~13× slower (~13 minutes).
2. **Structural reliability**: model produced valid JSON but wrong topology — `Comparison` + two `Modulo` nodes instead of `Modulo(15)` for the FizzBuzz "divisible by 3 and 5" case.

### Attempt 2 (2026-07-17) — JSON with `ChatOptions.ResponseFormat` schema + worked examples
Implemented with grammar-constrained JSON schema and FizzBuzz/CountDown worked examples in the system prompt. Results:
1. **Topology correct**: produced `Modulo(15)` as intended. Worked examples solved the structural reliability problem.
2. **Null properties**: several nodes had null required fields (`Condition`, `Template`, etc.) — model filled `kind` and `id` but omitted type-specific fields. Caused `NullReferenceException` in `EmbeddingPass.Describe`.
3. **Speed unchanged**: 444 seconds (7+ minutes) for FizzBuzz. `ChatOptions.ResponseFormat` constrained sampling did not reduce latency — the bottleneck is output token volume, not sampling overhead.

### Root cause of the speed problem
ministral-3b at 3B parameters generates ~8–10 tokens/second locally. A FizzBuzz graph JSON is ~800 tokens; at 10 tok/s that is ~80 seconds of pure generation time, before any network or decoding overhead. The 444s result suggests the constrained sampling was adding significant overhead on top. The `.spec` text is ~100 tokens → ~10 seconds.

### Path forward: compact intermediate format
The goal is an intermediate that is:
- **Short** (close to `.spec` token count, not JSON token count)
- **Structurally constrained** (model can't produce topologically wrong output)
- **Parseable without a growing custom parser** (unlike `.spec`, which requires a new branch per construct)

**Candidate: YAML node list with explicit edge notation.**
YAML eliminates JSON's syntactic overhead (braces, quotes on keys, closing tokens) while remaining structured. A FizzBuzz graph in YAML is approximately 40–50% fewer tokens than the equivalent JSON. The model has extensive YAML training data (k8s, GitHub Actions, docker-compose).

### Attempt 3 (2026-07-17) — YAML spike
Sent a compact YAML prompt to ministral-3b (604 input tokens) and measured generation time for FizzBuzz. Results:
1. **Speed worse, not better**: 411 seconds, **4039 output tokens** at 10.1 tok/s. The model generated the graph twice — it produced a first draft, then emitted inline prose ("Corrected the last edge..."), then emitted a second corrected draft. Total output is ~5× the expected YAML token count.
2. **Format violations**: output was fenced in `` ```yaml ``` `` despite explicit "no fences" instruction. Requires stripping.
3. **Hallucinated nodes**: introduced two boolean Variable nodes (`divisibleBy3`, `divisibleBy5`) not present in the source spec. Node count inflated even before the second-draft duplication.
4. **Invalid UUIDs**: generated hex digits outside `[0-9a-f]` (e.g. `g7h8i9j0`). UUID parsing would fail.
5. **Topology still wrong in first draft**: Modulo:3 and Modulo:5 present alongside Modulo:15 despite the explicit "ONE Modulo(15)" rule. Second draft partially corrected this but retained the stray nodes.

**Conclusion: YAML does not solve the problem.** The token count is not reduced in practice because the model self-corrects mid-output, inflating generation. The structural reliability and field-completeness problems persist. ministral-3b does not have the capacity to hold the full graph schema and input context simultaneously without degrading.

**Candidate: retain `.spec` but make it structurally isomorphic to the graph.**
Rather than a new format, extend `.spec` so that each construct maps 1:1 to a graph node. The parser becomes a graph builder, not a text interpreter. This avoids a new format but still requires growing the spec syntax per new node type.

**Decision gate (resolved)**: the YAML spike conclusively failed. Spec 21 is deferred until a larger extraction model is available — ministral-3b cannot reliably produce a complete, well-formed graph for FizzBuzz-complexity programs regardless of intermediate format. The `.spec` path remains the extraction mechanism.

## Motivation

The `.spec` format was a productive first step: a simple intermediate that the LLM could produce and a deterministic parser could consume. It proved the pipeline architecture. But it has a ceiling — every new language construct requires both a new spec syntax rule and a new parser branch. BubbleSort already pushed past that ceiling: the LLM produced a spec the parser couldn't fully handle, and we worked around it rather than fixing the format.

The real job of `SemanticGraphPass` is constructing a typed node graph. The LLM can do that directly given a structured output schema — the same JSON shape as `02-semantic-graph.json`. Asking it to go through an intermediate text format introduces a lossy encoding step and a fragile parser that trails the compiler's own type system.

This spec retires `.spec` as the Markdown compilation path. For Markdown input, `MarkdownSpecPass` produces the semantic graph directly. `ParseSpecPass` and `SemanticGraphPass` remain registered only for `.spec` input.

## What Changes

### MarkdownSpecPass

Replace the two-step extraction (Markdown → `.spec` → graph) with a single structured LLM call (Markdown → graph JSON):

1. **System prompt** describes the `SemanticGraph` JSON schema: nodes array with `kind` discriminators (`Program`, `Loop`, `Branch`, `Print`, `Modulo`, `Variable`, `Constant`, `Comparison`, `Array`, `Index`, `Swap`, `NestedLoop`), their required fields, and an edges array with `type` values (`Contains`, `Executes`, `Reads`, `Writes`, `TrueBranch`, `FalseBranch`, `DependsOn`, `Asserts`).

2. **LLM call** returns a JSON object matching the `SemanticGraph` wire format. Deserialize directly using the existing polymorphic JSON converters (same options as `SemanticGraphPass.LoadFromArtifactAsync`).

3. **Write artifact** `02-semantic-graph.json` directly — no `00-extracted.spec` or `01-spec.json`.

4. **Second LLM call** for authorial criteria is unchanged — still extracts acceptance rules from the prose.

5. **Populate context**: set `context.SemanticGraph` and `context.RawSpec = null` (no `.spec` text).

### ArtifactFile

`ArtifactFile` changes from `"00-extracted.spec"` to `"02-semantic-graph.json"`. The incremental skip check (`if artifact exists, skip pass`) now keys on the graph JSON, which is the true output of this pass.

### Pipeline registration

`Program.cs` changes for Markdown input: register only `MarkdownSpecPass` (which now produces the graph directly). Skip `ParseSpecPass` and `SemanticGraphPass` — they are not registered for the Markdown path.

For `.spec` input: register `ParseSpecPass` and `SemanticGraphPass` as before. `MarkdownSpecPass` is not registered.

```csharp
if (isMarkdown)
{
    builder.Services.AddTransient<ICompilerPass, MarkdownSpecPass>();
    // ParseSpecPass and SemanticGraphPass are skipped — MarkdownSpecPass produces the graph directly
}
else
{
    builder.Services
        .AddTransient<ICompilerPass, ParseSpecPass>()
        .AddTransient<ICompilerPass, SemanticGraphPass>();
}
```

### ValidateExtracted

Remove `ValidateExtracted`. The graph produced by the LLM is validated by `SemanticValidationPass` later in the pipeline. The old validation was a workaround for the fact that `SemanticGraphPass` would crash on malformed `.spec` — that problem goes away.

### ArtifactWriter

Update the `MarkdownSpecPass` case to write `02-semantic-graph.json` (same logic as the existing `SemanticGraphPass` case — nodes + edges JSON). Remove the `SemanticGraphPass` case from ArtifactWriter for the Markdown path (it's no longer called for Markdown input).

Actually: since both paths now write `02-semantic-graph.json`, add a shared helper `WriteSemanticGraph(ctx)` used by both `MarkdownSpecPass` and `SemanticGraphPass` cases.

## LLM Prompt Design

The system prompt must give the LLM enough type information to construct a valid graph without hallucinating fields. Include:

- The complete node type table (kind → required fields)
- The edge type list
- Two worked examples: a minimal CountDown graph (1 LoopNode, 1 VariableNode, 1 PrintNode) and the FizzBuzz graph (adds BranchNode, ModuloNode)
- Instruction to assign each node a fresh UUID v4 for the `id` field
- Instruction to return only the JSON object — no prose, no markdown fences

The prompt is deliberately narrow: it describes the current type system. As new node types are added to `Nodes.cs`, the prompt is updated to include them.

## Node Type Reference for System Prompt

```
Program:    { id, kind:"Program",    label, name }
Loop:       { id, kind:"Loop",       label, from, to }
Branch:     { id, kind:"Branch",     label, condition }
Print:      { id, kind:"Print",      label, template }
Modulo:     { id, kind:"Modulo",     label, divisor }
Variable:   { id, kind:"Variable",   label, name, type }
Constant:   { id, kind:"Constant",   label, value }
Comparison: { id, kind:"Comparison", label, op }
Array:      { id, kind:"Array",      label, name, elementType, size, values? }
Index:      { id, kind:"Index",      label, arrayName, indexExpr }
Swap:       { id, kind:"Swap",       label, arrayName, fromExpr, toExpr }
NestedLoop: { id, kind:"NestedLoop", label, variable, from, boundExpr }
```

Edge types: `Contains`, `Executes`, `Reads`, `Writes`, `TrueBranch`, `FalseBranch`, `DependsOn`, `Asserts`

## Acceptance Criterion

1. `./iron-llm examples/FizzBuzz` completes without error. No `00-extracted.spec` is written. `02-semantic-graph.json` is the first artifact.
2. `./iron-llm examples/BubbleSort` completes and the binary produces sorted output.
3. `./iron-llm examples/CountDown` completes and the binary produces `1..10`.
4. All existing tests pass (the FizzBuzz-family `ExampleProgramTests` use hardcoded spec text and bypass `MarkdownSpecPass` — they are unaffected).

## What Is Preserved

- `.spec` files remain a valid input format. `ParseSpecPass` + `SemanticGraphPass` still handle them.
- The `00-authorial-criteria.json` artifact and authorial assertion flow are unchanged.
- `SemanticNormalizationPass` still runs after embeddings — the LLM-produced labels may not be in canonical form, and the normalization pass corrects them.
- The existing `FizzBuzz.md`, `CountDown.md`, `Fizz.md`, `BubbleSort.md` examples are unchanged.

## Not In Scope

- Removing `ParseSpecPass` or `SemanticGraphPass` from the codebase — they remain for `.spec` input and for tests.
- Changing the `.spec` format — it still works as-is for the existing example programs.
- Structured output enforcement (e.g. constrained decoding) — prompt-based JSON is sufficient for ministral-3b on well-described schemas; add constrained output in a later spec if needed.
