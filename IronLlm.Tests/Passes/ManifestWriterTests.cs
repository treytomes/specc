using System.Text.Json;
using IronLlm.Passes;

namespace IronLlm.Tests.Passes;

public class ManifestWriterTests : IDisposable
{
    private readonly string _dir;

    public ManifestWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CompilationContext MakeContext(string? specPath = null) => new()
    {
        SpecPath     = specPath ?? Path.Combine(_dir, "input.spec"),
        ArtifactsDir = _dir,
    };

    [Fact]
    public async Task WriteAsync_CreatesManifestJson()
    {
        var ctx = MakeContext();
        await ManifestWriter.WriteAsync(ctx);
        Assert.True(File.Exists(Path.Combine(_dir, "manifest.json")));
    }

    [Fact]
    public async Task WriteAsync_IncludesSpecHash_WhenSpecExists()
    {
        var specPath = Path.Combine(_dir, "input.spec");
        await File.WriteAllTextAsync(specPath, "program: Test");

        var ctx = MakeContext(specPath);
        await ManifestWriter.WriteAsync(ctx);

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "manifest.json")));
        var specHash = doc.RootElement.GetProperty("specHash").GetString();
        Assert.NotNull(specHash);
        Assert.Equal(64, specHash.Length);   // 32 bytes hex = 64 chars
    }

    [Fact]
    public async Task WriteAsync_SpecHash_IsNull_WhenSpecMissing()
    {
        var ctx = MakeContext(Path.Combine(_dir, "nonexistent.spec"));
        await ManifestWriter.WriteAsync(ctx);

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "manifest.json")));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("specHash").ValueKind);
    }

    [Fact]
    public async Task WriteAsync_OnlyIncludesExistingArtifacts()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "04-cfg.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_dir, "05-stackir.json"), "[]");

        var ctx = MakeContext();
        await ManifestWriter.WriteAsync(ctx);

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "manifest.json")));
        var passes = doc.RootElement.GetProperty("passes").EnumerateArray().ToList();
        Assert.Equal(2, passes.Count);
        Assert.Equal("04-cfg.json", passes[0].GetProperty("artifact").GetString());
        Assert.Equal("05-stackir.json", passes[1].GetProperty("artifact").GetString());
    }

    [Fact]
    public async Task WriteAsync_ArtifactsInDeclarationOrder()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "06-program.il"), ".assembly {}");
        await File.WriteAllTextAsync(Path.Combine(_dir, "01-spec.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_dir, "04-cfg.json"), "{}");

        var ctx = MakeContext();
        await ManifestWriter.WriteAsync(ctx);

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "manifest.json")));
        var names = doc.RootElement.GetProperty("passes")
            .EnumerateArray()
            .Select(e => e.GetProperty("artifact").GetString())
            .ToList();

        Assert.Equal(["01-spec.json", "04-cfg.json", "06-program.il"], names);
    }

    [Fact]
    public async Task WriteAsync_EachPassHas64CharHex()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "04-cfg.json"), "{\"blocks\":[]}");

        var ctx = MakeContext();
        await ManifestWriter.WriteAsync(ctx);

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "manifest.json")));
        var sha = doc.RootElement.GetProperty("passes")[0].GetProperty("sha256").GetString();
        Assert.NotNull(sha);
        Assert.Equal(64, sha.Length);
        Assert.Matches("^[0-9a-f]+$", sha);
    }

    [Fact]
    public async Task HashFile_IsDeterministic()
    {
        var path = Path.Combine(_dir, "sample.txt");
        await File.WriteAllTextAsync(path, "hello world");

        var h1 = ManifestWriter.HashFile(path);
        var h2 = ManifestWriter.HashFile(path);

        Assert.Equal(h1, h2);
    }

    [Fact]
    public async Task HashFile_DiffersForDifferentContent()
    {
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        await File.WriteAllTextAsync(a, "hello");
        await File.WriteAllTextAsync(b, "world");

        Assert.NotEqual(ManifestWriter.HashFile(a), ManifestWriter.HashFile(b));
    }

    [Fact]
    public async Task WriteAsync_EmptyArtifactsDir_ProducesEmptyPassesList()
    {
        var ctx = MakeContext();
        await ManifestWriter.WriteAsync(ctx);

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "manifest.json")));
        Assert.Equal(0, doc.RootElement.GetProperty("passes").GetArrayLength());
    }
}
