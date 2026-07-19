using System.Text.Json;
using Specc.Graph;
using Specc.Passes;
using Specc.Passes.Repository;

namespace Specc.Tests.Passes;

public class GraphRepositoryTests : IDisposable
{
    private readonly string _repoDir;
    private readonly string _artifactsDir;

    public GraphRepositoryTests()
    {
        _repoDir      = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _artifactsDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_artifactsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoDir))
            Directory.Delete(_repoDir, recursive: true);
        if (Directory.Exists(_artifactsDir))
            Directory.Delete(_artifactsDir, recursive: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private CompilationContext MakeContext(string? specContent = null)
    {
        var specPath = Path.Combine(_artifactsDir, "test.spec");
        File.WriteAllText(specPath, specContent ?? "program: Test");

        var ctx = new CompilationContext
        {
            SpecPath       = specPath,
            ArtifactsDir   = _artifactsDir,
            RepositoryPath = _repoDir,
        };

        // Add a simple ProgramNode so PersistAsync can resolve a program name.
        var graph = new SemanticGraph();
        graph.Add(new ProgramNode(Guid.NewGuid(), "Program:Test", "Test"));
        ctx.SemanticGraph = graph;

        return ctx;
    }

    private static void WriteEmbeddingsArtifact(string dir, IEnumerable<(Guid id, string label, float[] vector)> entries)
    {
        var data = entries.Select(e => new
        {
            nodeId     = e.id,
            label      = e.label,
            dimensions = e.vector.Length,
            vector     = e.vector,
        });
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, "03-embeddings.json"), json);
    }

    // ── PersistAsync tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task PersistAsync_CreatesIndexJson()
    {
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir,
        [
            (Guid.NewGuid(), "Program:Test", [1f, 0f, 0f]),
        ]);

        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(Path.Combine(_repoDir, "index.json")));
    }

    [Fact]
    public async Task PersistAsync_WritesPerHashSubdirectory()
    {
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir,
        [
            (Guid.NewGuid(), "Program:Test", [1f, 0f, 0f]),
        ]);

        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        // Should have at least one subdirectory with an 8-char hex name.
        var subdirs = Directory.GetDirectories(_repoDir);
        Assert.Single(subdirs);
        Assert.Equal(8, Path.GetFileName(subdirs[0]).Length);
    }

    [Fact]
    public async Task PersistAsync_CopiesEmbeddingsArtifact()
    {
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir,
        [
            (Guid.NewGuid(), "Program:Test", [1f, 0f, 0f]),
        ]);

        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        var subdirs = Directory.GetDirectories(_repoDir);
        Assert.Single(subdirs);
        Assert.True(File.Exists(Path.Combine(subdirs[0], "03-embeddings.json")));
    }

    [Fact]
    public async Task PersistAsync_SkipsDuplicateSpecHash()
    {
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir,
        [
            (Guid.NewGuid(), "Program:Test", [1f, 0f, 0f]),
        ]);

        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);
        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);  // second call

        var indexJson = await File.ReadAllTextAsync(Path.Combine(_repoDir, "index.json"));
        var index = JsonSerializer.Deserialize<RepositoryIndex>(
            indexJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        // Should only have one entry despite two calls.
        Assert.Single(index.Units);
    }

    [Fact]
    public async Task PersistAsync_IndexContainsCorrectProgramName()
    {
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir,
        [
            (Guid.NewGuid(), "Program:Test", [1f, 0f, 0f]),
        ]);

        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        var indexJson = await File.ReadAllTextAsync(Path.Combine(_repoDir, "index.json"));
        var index = JsonSerializer.Deserialize<RepositoryIndex>(
            indexJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("Test", index.Units[0].ProgramName);
    }

    // ── FindSimilarAsync tests ────────────────────────────────────────────────────

    [Fact]
    public async Task FindSimilarAsync_ReturnsEmpty_WhenRepositoryDoesNotExist()
    {
        var ctx = MakeContext();
        ctx.RepositoryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()); // non-existent

        var results = await GraphRepository.FindSimilarAsync(ctx);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarAsync_ReturnsEmpty_WhenIndexIsAbsent()
    {
        var ctx = MakeContext();
        // _repoDir exists but has no index.json

        var results = await GraphRepository.FindSimilarAsync(ctx);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarAsync_ReturnsEmpty_WhenCurrentEmbeddingsEmpty()
    {
        var nodeId = Guid.NewGuid();
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir, [(nodeId, "Program:Test", [1f, 0f, 0f])]);

        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        // Build a fresh context pointing at the same repo but with no embeddings.
        var queryCtx = new CompilationContext
        {
            SpecPath       = ctx.SpecPath,
            ArtifactsDir   = _artifactsDir,
            RepositoryPath = _repoDir,
        };
        // Embeddings intentionally left empty.

        var results = await GraphRepository.FindSimilarAsync(queryCtx);
        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarAsync_ReturnsPriorsAboveThreshold()
    {
        var storedId = Guid.NewGuid();

        // Persist a unit with a known vector.
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir, [(storedId, "Program:Test", [1f, 0f, 0f])]);
        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        // Query with a very similar vector — cosine similarity ≈ 1.0.
        var queryCtx = new CompilationContext
        {
            SpecPath       = ctx.SpecPath,
            ArtifactsDir   = _artifactsDir,
            RepositoryPath = _repoDir,
            Embeddings     = [new NodeEmbedding(Guid.NewGuid(), "Program:Test", [0.99f, 0.01f, 0f])],
        };

        var results = await GraphRepository.FindSimilarAsync(queryCtx, threshold: 0.85f);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.Similarity >= 0.85f));
    }

    [Fact]
    public async Task FindSimilarAsync_FiltersOutBelowThreshold()
    {
        var storedId = Guid.NewGuid();

        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir, [(storedId, "Program:Test", [1f, 0f, 0f])]);
        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        // Query with an orthogonal vector — cosine similarity = 0.
        var queryCtx = new CompilationContext
        {
            SpecPath       = ctx.SpecPath,
            ArtifactsDir   = _artifactsDir,
            RepositoryPath = _repoDir,
            Embeddings     = [new NodeEmbedding(Guid.NewGuid(), "Loop:1..100", [0f, 1f, 0f])],
        };

        var results = await GraphRepository.FindSimilarAsync(queryCtx, threshold: 0.85f);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarAsync_RespectsTopKLimit()
    {
        // Store a unit with 5 nodes all similar to the query.
        var ctx = MakeContext();
        var entries = Enumerable.Range(0, 5)
            .Select(i => (Guid.NewGuid(), $"Node{i}", new float[] { 1f, 0f, 0f }))
            .ToList();
        WriteEmbeddingsArtifact(_artifactsDir, entries);
        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        var queryCtx = new CompilationContext
        {
            SpecPath       = ctx.SpecPath,
            ArtifactsDir   = _artifactsDir,
            RepositoryPath = _repoDir,
            Embeddings     = [new NodeEmbedding(Guid.NewGuid(), "QueryNode", [1f, 0f, 0f])],
        };

        var results = await GraphRepository.FindSimilarAsync(queryCtx, threshold: 0.85f, topK: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task FindSimilarAsync_ResultsSortedByDescendingSimilarity()
    {
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir,
        [
            (Guid.NewGuid(), "NodeA", [1f, 0f, 0f]),   // cos(query) = 1.0
            (Guid.NewGuid(), "NodeB", [0.9f, 0.1f, 0f]), // lower similarity
        ]);
        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        var queryCtx = new CompilationContext
        {
            SpecPath       = ctx.SpecPath,
            ArtifactsDir   = _artifactsDir,
            RepositoryPath = _repoDir,
            Embeddings     = [new NodeEmbedding(Guid.NewGuid(), "Query", [1f, 0f, 0f])],
        };

        var results = await GraphRepository.FindSimilarAsync(queryCtx, threshold: 0.85f);

        Assert.True(results.Count >= 2);
        for (var i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Similarity >= results[i].Similarity);
    }

    // ── Round-trip test ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_PersistThenRetrieve_FindsSameNode()
    {
        var storedId    = Guid.NewGuid();
        var storedLabel = "Program:FizzBuzz";
        var vector      = new float[] { 1f, 0f, 0f };

        // Persist a unit containing one embedding.
        var ctx = MakeContext();
        WriteEmbeddingsArtifact(_artifactsDir, [(storedId, storedLabel, vector)]);
        await GraphRepository.PersistAsync(ctx, DateTimeOffset.UtcNow);

        // Retrieve using a nearly identical query vector (similarity ≈ 1.0).
        var queryCtx = new CompilationContext
        {
            SpecPath       = ctx.SpecPath,
            ArtifactsDir   = _artifactsDir,
            RepositoryPath = _repoDir,
            Embeddings     = [new NodeEmbedding(Guid.NewGuid(), storedLabel, [0.99f, 0.01f, 0f])],
        };

        var results = await GraphRepository.FindSimilarAsync(queryCtx, threshold: 0.85f);

        Assert.NotEmpty(results);
        var hit = results.First();
        Assert.Equal(storedId, hit.NodeId);
        Assert.Equal(storedLabel, hit.NodeLabel);
        Assert.True(hit.Similarity >= 0.85f);
    }
}
