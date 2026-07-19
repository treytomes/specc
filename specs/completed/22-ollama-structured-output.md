# Spec 22 — Ollama Structured Output via JSON Schema

**Status:** Incomplete  
**Scope:** `MarkdownSpecPass.cs`, `Specc.csproj`; all other passes unchanged

## Motivation

`MarkdownSpecPass` makes two LLM calls:

1. **Spec extraction** — prompt → ~100 tokens of `.spec` text
2. **Criteria extraction** — prompt → ~80 tokens of JSON (or is now skipped for array programs)

Both rely on instructing the model to "return only valid JSON / .spec format" in the prompt. The model sometimes wraps output in markdown fences, appends explanatory text, or produces subtle format deviations that require post-processing. The fence-stripping code in `ExtractAuthorialCriteriaAsync` is a symptom.

Ollama's `format` parameter feeds a JSON Schema to llama.cpp's GBNF grammar sampler. The model can only emit tokens that satisfy the schema. This eliminates:

- Fenced code block wrapping
- Trailing prose
- Wrong property names
- Missing required fields

For the `.spec` extraction call this doesn't help (`.spec` is not JSON). For the criteria call it eliminates the fence-stripping workaround. More importantly, it is the prerequisite for revisiting Spec 21 (direct graph extraction): with a schema that enumerates valid `kind` discriminator values and per-type required fields, the model cannot produce structurally invalid graph topology.

## What the M.E.AI API Provides

`ChatOptions.ResponseFormat` accepts a `ChatResponseFormat`. The relevant factory is:

```csharp
ChatResponseFormat.ForJsonSchema(JsonElement schema, string? schemaName, string? schemaDescription)
```

`ChatResponseFormatJson` serializes to Ollama's `format` field in the request body. When `format` contains a schema, llama.cpp uses grammar-based sampling constrained to that schema.

`Microsoft.Extensions.AI.Abstractions` 10.0.x is already in the project's dependency graph.

## What Changes

### Criteria extraction — remove fence-stripping workaround

In `ExtractAuthorialCriteriaAsync`, replace the manual fence-stripping with a `ChatOptions` that constrains the response to the criteria schema:

```csharp
private static readonly JsonElement CriteriaSchema = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "loopFrom": { "type": "integer" },
        "loopTo":   { "type": "integer" },
        "rules": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "divisor":   { "type": "integer" },
              "isDefault": { "type": "boolean" },
              "expected":  { "type": "string"  }
            }
          }
        }
      }
    }
    """).RootElement;

private static readonly ChatOptions CriteriaOptions = new()
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema(CriteriaSchema, "AuthorialCriteria", null),
};
```

Pass `CriteriaOptions` as the second argument to `_chat.GetResponseAsync`. Remove the fence-stripping block in `ExtractAuthorialCriteriaAsync` — it is no longer needed. Keep the outer try/catch for the case where an older Ollama version doesn't support the `format` parameter and returns an error.

### Static readonly initialization

`ChatResponseFormat.ForJsonSchema` and `JsonDocument.Parse` are safe to call at type-init time (no I/O, deterministic). Declare both `CriteriaSchema` and `CriteriaOptions` as `private static readonly` fields on `MarkdownSpecPass`.

### No schema changes to `AuthorialCriteriaDto`

The schema mirrors the existing DTO. No property renames.

## What Does Not Change

- The `.spec` extraction call (`ExtractSpecAsync`) — `.spec` is plain text, not JSON; constrained output doesn't apply.
- `ParseExpectedOutputBlock` — already deterministic, no LLM involvement.
- `EvaluateRules` — pure C#, unaffected.
- `ValidateExtracted` — unchanged.
- All other passes — no changes.

## Acceptance Criteria

1. `scripts/test.sh` passes with all tests green.
2. `scripts/run.sh examples/FizzBuzz` completes. `00-authorial-criteria.json` is valid JSON with no fencing artifacts, matching the prior output.
3. Removing `00-authorial-criteria.json` and re-running does not require fence-stripping in the source code (the `if (text.StartsWith("```"))` block is gone).
4. If Ollama is older than the version that added `format`-with-schema support, the catch block falls through to `new AuthorialCriteriaDto()` gracefully (no pipeline crash, just falls back to graph-derived criteria).

## Not In Scope

- Spec extraction constrained output — `.spec` is not a JSON format.
- Graph extraction constrained output — that is Spec 21's job, which depends on this spec as a prerequisite.
- Any change to how the Ollama endpoint or model is configured.
