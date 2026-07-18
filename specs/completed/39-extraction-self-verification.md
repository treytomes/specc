# Spec 39 — Extraction Self-Verification

**Status:** Completed
**Scope:** `MarkdownSpecPass.cs`

## Motivation

The classifier selects construct families; the extractor produces a `.spec`. There is currently no check that the two are consistent. If the classifier says `["input","arithmetic","while"]` but the extracted spec contains no `while:` block, the pipeline proceeds silently and compiles a broken binary. The failure surfaces later — as a hanging acceptance verification or a wrong output — with no indication that extraction was the source.

A cheap post-extraction consistency check between the classifier's tags and the extracted spec text would catch the most common extraction failure mode at the point of failure, before any downstream passes run.

## Design

After `ExtractSpecAsync` returns, scan the extracted spec text for the presence of each tag's required keyword:

| Tag | Required keyword in spec |
|-----|--------------------------|
| `while` | `while:` |
| `arithmetic` | `assign:` |
| `array` | `initial_value:` or `array[int]` |
| `input` | `source: stdin` |
| `branch` | `branch:` |
| `loop` | `loop:` |

If a tag was classified as needed but its keyword is absent from the extracted spec, log a warning at `[WRN]` level listing the missing constructs. Do not throw — the extraction may be partially correct and worth proceeding with. This is a diagnostic, not a gate.

Optionally (if `--verbose`), also log the inverse: keywords present in the spec that were not in the classifier's tags. This catches cases where the extractor hallucinated a construct the classifier didn't expect (e.g. a spurious `loop:` in a `while:`-only program).

## Implementation

Add a `VerifyConsistency(string[] tags, string specText)` method to `MarkdownSpecPass`, called between `ExtractSpecAsync` and `ValidateExtracted`:

```csharp
private void VerifyConsistency(string[] tags, string specText)
{
    var missing = new List<string>();
    if (tags.Contains("while")      && !specText.Contains("while:"))      missing.Add("while:");
    if (tags.Contains("arithmetic") && !specText.Contains("assign:"))     missing.Add("assign:");
    if (tags.Contains("input")      && !specText.Contains("source: stdin")) missing.Add("source: stdin");
    if (tags.Contains("array")      && !specText.Contains("initial_value:") && !specText.Contains("array[int]"))
        missing.Add("array construct");

    if (missing.Count > 0)
        _logger.LogWarning(
            "Extraction may be incomplete — classifier expected [{Tags}] but spec is missing: {Missing}",
            string.Join(", ", tags), string.Join(", ", missing));
}
```

## Acceptance criteria

1. A Collatz extraction that produces no `while:` logs `[WRN] Extraction may be incomplete — classifier expected [input, arithmetic, while] but spec is missing: while:`.
2. A FizzBuzz extraction with correct `loop:` and `branch:` logs no warning.
3. No existing tests are broken.

## What this enables

- Immediately visible signal when extraction silently drops a construct.
- Future: the warning can be promoted to a retry trigger (re-run `ExtractSpecAsync` with a stronger hint prompt) without changing any other code.
