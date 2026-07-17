# Spec 21 — Direct Graph Extraction from Markdown

**Status:** Deferred — requires a stronger model or constrained decoding  
**Scope:** `MarkdownSpecPass.cs`, `Program.cs`, `ArtifactWriter.cs`; `ParseSpecPass` and `SemanticGraphPass` become `.spec`-only fallbacks

## Implementation Notes (2026-07-16)

Attempted with ministral-3b. Two blocking issues discovered:

1. **Speed**: asking the model to generate ~600 tokens of structured JSON (vs ~100 tokens for the old `.spec` text) made compilation ~13× slower (13 minutes vs ~1 minute). Even with compact integer IDs instead of UUIDs, the output volume is the bottleneck.

2. **Structural reliability**: ministral-3b doesn't reliably produce the graph topology the pipeline requires. For FizzBuzz's "divisible by both 3 and 5" condition it generated a `Comparison` node and two separate `Modulo` nodes (mod3, mod5) with a `DependsOn → Comparison` edge, instead of a single `Modulo(15)` with `DependsOn → Modulo`. This structurally valid but pipeline-incompatible representation broke `AcceptanceCriteriaPass` and `CfgPass`.

Viable with: mistral-7b or qwen2.5-coder:7b (higher instruction fidelity), or constrained JSON decoding (grammar-based sampling to enforce schema compliance). Not viable with ministral-3b as the sole extraction model.

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
