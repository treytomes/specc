# Spec 02 — Artifact Hashing and Reproducibility

**Status:** Ready to implement  
**Scope:** `ArtifactWriter.cs`, new `HashManifest.cs`

## Goal

Every artifact gets a SHA-256 hash written alongside it. A `manifest.json` records the hash of each stage's output so that:

- Re-runs that change only the CFG show exactly what changed.
- A given spec input can be tied to a deterministic chain of artifact hashes.
- Future tooling can skip re-running a pass if its input hash has not changed.

## Manifest Shape

```json
{
  "specHash": "abc123...",
  "passes": [
    { "artifact": "00-extracted.spec",          "sha256": "..." },
    { "artifact": "00-authorial-criteria.json", "sha256": "..." },
    { "artifact": "00-acceptance.json",          "sha256": "..." },
    { "artifact": "01-spec.json",                "sha256": "..." },
    { "artifact": "02-semantic-graph.json",      "sha256": "..." },
    { "artifact": "02b-semantic-graph.mmd",      "sha256": "..." },
    { "artifact": "02c-semantic-graph.svg",      "sha256": "..." },
    { "artifact": "03-embeddings.json",          "sha256": "..." },
    { "artifact": "03b-normalized-graph.json",   "sha256": "..." },
    { "artifact": "04-cfg.json",                 "sha256": "..." },
    { "artifact": "05-stackir.json",             "sha256": "..." },
    { "artifact": "06-program.il",               "sha256": "..." },
    { "artifact": "07-program.dll",              "sha256": "..." }
  ]
}
```

Written to `<artifacts-dir>/manifest.json` after all other artifacts.

## Implementation

Add `HashManifest` record and `ManifestWriter` static class. `ArtifactWriter` calls it after the final pass.

```csharp
public static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    var hash = SHA256.HashData(stream);
    return Convert.ToHexStringLower(hash);
}
```

No new dependencies — `System.Security.Cryptography` is in-box. Only include artifacts that exist (optional passes like `MarkdownSpecPass` may not run).

## Pass-Level Caching (future)

Once hashes exist, `ICompilerPass` can gain an optional `string? InputArtifact` property. Before executing, `Pipeline` checks whether the input artifact's hash matches the last run's manifest. If it matches and the output artifact already exists, the pass is skipped regardless of file timestamps. This makes re-running after a spec edit skip the embedding pass only when its actual input has not changed — stricter than the current filename-existence check.

This caching behavior is out of scope for this spec — hashing is the prerequisite.
