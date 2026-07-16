using IronLlm.Passes;
using IronLlm.Tests.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronLlm.Tests.Passes;

public class MarkdownSpecPassTests
{
    private static readonly string ValidSpec = PipelineFixtures.FizzBuzzSpecText;

    private static readonly string SampleMarkdown = """
        # FizzBuzz
        Write a program that iterates from 1 to 100.
        For multiples of 3 print "Fizz", multiples of 5 print "Buzz",
        both print "FizzBuzz", otherwise print the number.
        """;

    // Chat client that returns a fixed response.
    private sealed class StubChatClient(string response) : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static MarkdownSpecPass MakePass(string chatResponse) =>
        new(new StubChatClient(chatResponse), NullLogger<MarkdownSpecPass>.Instance);

    private static async Task<CompilationContext> RunPassAsync(
        string chatResponse, string? specContent = null)
    {
        var tmp = Path.GetTempFileName() + ".md";
        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await File.WriteAllTextAsync(tmp, specContent ?? SampleMarkdown);
            var ctx = new CompilationContext
            {
                SpecPath     = tmp,
                ArtifactsDir = outDir,
            };
            Directory.CreateDirectory(outDir);
            await MakePass(chatResponse).ExecuteAsync(ctx);
            return ctx;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task Execute_UpdatesSpecPath_ToExtractedArtifact()
    {
        var ctx = await RunPassAsync(ValidSpec);
        Assert.EndsWith("00-extracted.spec", ctx.SpecPath);
        Directory.Delete(ctx.ArtifactsDir, recursive: true);
    }

    [Fact]
    public async Task Execute_WritesArtifactToDisk()
    {
        var ctx = await RunPassAsync(ValidSpec);
        Assert.True(File.Exists(ctx.SpecPath));
        Directory.Delete(ctx.ArtifactsDir, recursive: true);
    }

    [Fact]
    public async Task Execute_ArtifactContent_MatchesExtractedSpec()
    {
        var ctx     = await RunPassAsync(ValidSpec);
        var content = await File.ReadAllTextAsync(ctx.SpecPath);
        Assert.Contains("program:", content);
        Directory.Delete(ctx.ArtifactsDir, recursive: true);
    }

    [Fact]
    public async Task Execute_Throws_WhenLlmReturnsError()
    {
        await Assert.ThrowsAsync<CompilationException>(() =>
            RunPassAsync("ERROR: cannot express this program in .spec format"));
    }

    [Fact]
    public async Task Execute_Throws_WhenLlmReturnsInvalidSpec()
    {
        // Valid-looking text but won't parse to a graph with a ProgramNode.
        await Assert.ThrowsAsync<CompilationException>(() =>
            RunPassAsync("this is not a spec file at all"));
    }

    [Fact]
    public async Task Execute_Throws_WhenMarkdownFileNotFound()
    {
        var ctx = new CompilationContext
        {
            SpecPath     = "/does/not/exist.md",
            ArtifactsDir = Path.GetTempPath(),
        };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            MakePass(ValidSpec).ExecuteAsync(ctx));
    }

    [Fact]
    public async Task LoadFromArtifact_SetsSpecPath()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, ValidSpec);
            var ctx = PipelineFixtures.MakeContext();
            await MakePass(ValidSpec).LoadFromArtifactAsync(tmp, ctx);
            Assert.Equal(tmp, ctx.SpecPath);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ArtifactFile_IsExpectedFilename()
    {
        Assert.Equal("00-extracted.spec", MakePass("").ArtifactFile);
    }
}
