# Spec 44 — Repository Health: Retroactive Eviction and Retrieval Quality

## Status

Incomplete.

## Context

Level 1 repository poisoning prevention (Spec 44 predecessor, implemented in commit 8edaed2) gates `RepositoryPersistPass` on `AcceptancePassed == true`, preventing failed compilations from entering the repository. `FindPriorsByTagsAsync` ranks verified entries (`AcceptancePassed && AssertionCount > 0`) above unverified at the same tag-overlap score.

That handles the forward path. Two open problems remain:

1. **Retroactive eviction**: a compilation that was accepted in the past may become wrong if the extraction pipeline is later corrected (new pass added, normalization threshold tuned, LLM model swapped). The stored spec and artifacts reflect a prior compiler version and can mislead future extractions.

2. **Retrieval quality signal**: `AssertionCount` already exists on `CompiledUnit`, but it is not surfaced in the extraction prompt or used to rank retrieval results beyond the boolean `AcceptancePassed` flag. A unit with 100/100 assertions should rank above one with 3/3 when both match the query.

---

## Level 2 — Retroactive Eviction on Recompilation Failure

### Trigger

When `AcceptanceVerificationPass` throws `AcceptanceFailureException` for a program whose name already exists in the repository, mark that entry stale rather than silently ignoring it. A new compilation of the same program name that fails acceptance is evidence that the stored spec was wrong or has become wrong.

### Design

Add `EvictByProgramNameAsync(string programName, string repositoryPath)` to `GraphRepository`:

```csharp
public static async Task EvictByProgramNameAsync(string programName, string repositoryPath)
{
    var indexPath = Path.Combine(repositoryPath, "index.json");
    var index     = await LoadIndexAsync(indexPath);
    var before    = index.Units.Count;
    index.Units.RemoveAll(u => u.ProgramName == programName);
    if (index.Units.Count != before)
    {
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(index, JsonOpts));
        // log eviction count
    }
}
```

Call site: `RepositoryPersistPass.ExecuteAsync` — when `AcceptancePassed == false` **and** the program name already exists in the index, call `EvictByProgramNameAsync` before returning.

```csharp
if (context.AcceptancePassed == false)
{
    _logger.LogWarning("Repository: skipping persist — acceptance verification failed");
    var progName = context.SemanticGraph?.Nodes.OfType<ProgramNode>()
                          .Select(n => n.Name).FirstOrDefault();
    if (progName != null)
        await GraphRepository.EvictByProgramNameAsync(progName, context.RepositoryPath);
    return;
}
```

### Guard

Only evict when the new compilation ran acceptance verification all the way to failure (not when it crashed in an earlier pass). `AcceptancePassed == false` only occurs when `AcceptanceVerificationPass` executes and assertions differ from output — earlier exceptions leave it `null`.

### Trade-offs

- **False eviction risk**: a transient infra failure (Ollama offline, model crash) could produce a bad extraction and evict a good prior. Mitigate by requiring at least one prior assertion count > 0 on the stored entry before evicting — a unit with `AssertionCount == 0` is already unverified and lower-risk to remove.
- **Spec hash granularity**: `SpecHash` is the hash of the raw `.spec` file, not the `.md` file. Two `.md` files that extract to different specs for the same program produce different hashes. Eviction by program name is therefore coarser than the hash-based persist guard — it removes all entries for that name, which is intentional (if the new extraction fails, we don't know which prior was correct).

---

## Level 3 — Assertion-Weighted Retrieval

### Motivation

`FindPriorsByTagsAsync` currently ranks by: (1) tag overlap score, (2) `AcceptancePassed && AssertionCount > 0` as a binary tier, (3) `SpecText.Length`. This means a 100-assertion FizzBuzz and a 3-assertion Fizz rank identically in tier 2 if both match the query tags.

Higher `AssertionCount` is a stronger signal of correctness and coverage. It should lift entries within the verified tier.

### Design

Replace the secondary sort with a continuous score:

```csharp
.OrderByDescending(x => x.Score)                              // tag overlap (primary)
.ThenByDescending(x => x.Verified ? x.AssertionCount : -1)   // assertion-weighted quality
.ThenByDescending(x => x.SpecText.Length)                     // tiebreak: more content
```

Where `Verified = u.AcceptancePassed && u.AssertionCount > 0` as before. Unverified entries (`AssertionCount == 0`) get score `-1` and sort below all verified entries regardless of count.

### Extension: quality threshold

Add an optional `minAssertions` parameter to `FindPriorsByTagsAsync`. When non-zero, filter out entries with `AssertionCount < minAssertions`. The call site in `MarkdownSpecPass` can set this to 1 or higher once the repository is populated with verified compilations, progressively tightening retrieval quality as the repository grows.

```csharp
public static async Task<List<(string ProgramName, string SpecText)>> FindPriorsByTagsAsync(
    string repositoryPath, string[] tags, int topK = 2, int minAssertions = 0)
```

Default `0` preserves the current behaviour (all verified entries eligible).

---

## Implementation order

Level 2 (eviction) should be implemented before Level 3 (weighted retrieval), since eviction cleans the input to the ranking function. Together they are small: `~30` lines of production code, no schema changes (all fields already exist on `CompiledUnit`).

Level 3 is a one-line change to the `OrderByDescending` chain.

## Tests

- `RepositoryEviction`: compile a program, persist it (acceptance passed), re-compile with a bad extraction (acceptance fails), assert the entry is removed from the index.
- `FindPriorsByTagsAsync_PreferHighAssertionCount`: two verified entries at the same tag score, different `AssertionCount`; higher count ranks first.
- `FindPriorsByTagsAsync_MinAssertionsFilter`: entry with `AssertionCount == 0` excluded when `minAssertions = 1`.
