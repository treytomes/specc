using Specc.Passes;
using Specc.Tests.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Specc.Tests.Passes;

public class MarkdownSpecPassTests
{
    private static readonly string ValidSpec = PipelineFixtures.FizzBuzzSpecText;

    private static readonly string SampleMarkdown = """
        # FizzBuzz
        Write a program that iterates from 1 to 100.
        For multiples of 3 print "Fizz", multiples of 5 print "Buzz",
        both print "FizzBuzz", otherwise print the number.
        """;

    // Returns the same response for every call.
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

    // Returns queued responses in order; the last queued response is repeated if exhausted.
    private sealed class QueuedStubChatClient(params string[] responses) : IChatClient
    {
        private int _index;
        public ChatClientMetadata Metadata => new("stub", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var i   = Math.Min(_index++, responses.Length - 1);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responses[i])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // Classifier response returned for call 1; chatResponse for call 2 (extraction) onward.
    private static MarkdownSpecPass MakePass(string chatResponse) =>
        new(new QueuedStubChatClient("""["loop","branch"]""", chatResponse), NullLogger<MarkdownSpecPass>.Instance);

    // Prepend a classifier response so tests don't need to supply it explicitly.
    private static MarkdownSpecPass MakeQueuedPass(params string[] responses) =>
        new(new QueuedStubChatClient(["""["loop","branch"]""", ..responses]), NullLogger<MarkdownSpecPass>.Instance);

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

    // ── Authorial criteria extraction ─────────────────────────────────────────

    private static readonly string ValidCriteriaJson = """
        {
          "loopFrom": 1,
          "loopTo": 100,
          "rules": [
            { "divisor": 15, "expected": "FizzBuzz" },
            { "divisor": 3,  "expected": "Fizz"     },
            { "divisor": 5,  "expected": "Buzz"     },
            { "isDefault": true, "expected": "{n}"  }
          ]
        }
        """;

    private static async Task<(CompilationContext ctx, string artifactsDir)> RunQueuedPassAsync(
        string specResponse, string criteriaResponse)
    {
        var tmp    = Path.GetTempFileName() + ".md";
        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(tmp, SampleMarkdown);
        var ctx = new CompilationContext { SpecPath = tmp, ArtifactsDir = outDir };
        await MakeQueuedPass(specResponse, criteriaResponse).ExecuteAsync(ctx);
        File.Delete(tmp);
        return (ctx, outDir);
    }

    [Fact]
    public async Task Execute_PopulatesAuthorialAssertions_WhenCriteriaExtracted()
    {
        var (ctx, dir) = await RunQueuedPassAsync(ValidSpec, ValidCriteriaJson);
        try
        {
            Assert.Equal(100, ctx.AuthorialAssertions.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Execute_AuthorialAssertions_AreCorrect()
    {
        var (ctx, dir) = await RunQueuedPassAsync(ValidSpec, ValidCriteriaJson);
        try
        {
            Assert.Equal("Fizz",     ctx.AuthorialAssertions[2].Expected);   // iteration 3
            Assert.Equal("Buzz",     ctx.AuthorialAssertions[4].Expected);   // iteration 5
            Assert.Equal("FizzBuzz", ctx.AuthorialAssertions[14].Expected);  // iteration 15
            Assert.Equal("1",        ctx.AuthorialAssertions[0].Expected);   // iteration 1
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Execute_WritesCriteriaFileToDisk_WhenRulesPresent()
    {
        var (ctx, dir) = await RunQueuedPassAsync(ValidSpec, ValidCriteriaJson);
        try
        {
            var criteriaPath = Path.Combine(dir, "00-authorial-criteria.json");
            Assert.True(File.Exists(criteriaPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Execute_AuthorialAssertions_EmptyWhenModelReturnsEmptyObject()
    {
        var (ctx, dir) = await RunQueuedPassAsync(ValidSpec, "{}");
        try
        {
            Assert.Empty(ctx.AuthorialAssertions);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoadFromArtifact_RestoresAuthorialAssertions_WhenFilePresent()
    {
        var (ctx1, dir) = await RunQueuedPassAsync(ValidSpec, ValidCriteriaJson);
        try
        {
            // Simulate a re-run: build a fresh context and load from artifact.
            var specArtifact = Path.Combine(dir, "00-extracted.spec");
            var ctx2 = PipelineFixtures.MakeContext();
            await MakeQueuedPass(ValidSpec, ValidCriteriaJson)
                .LoadFromArtifactAsync(specArtifact, ctx2);
            Assert.Equal(100, ctx2.AuthorialAssertions.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── ParseExpectedOutputBlock (via full-pass execution) ───────────────────

    private static readonly string BubbleSortMarkdown = """
        # BubbleSort

        Sort an array.

        ## Expected Output
        ```
        3
        11
        12
        ```
        """;

    [Fact]
    public async Task Execute_UsesDirectOutputBlock_WhenPresent()
    {
        // The pass should parse "## Expected Output" lines directly, never calling
        // the LLM criteria extraction for such documents.
        var tmp    = Path.GetTempFileName() + ".md";
        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        try
        {
            await File.WriteAllTextAsync(tmp, BubbleSortMarkdown);
            var ctx = new CompilationContext { SpecPath = tmp, ArtifactsDir = outDir };
            // First response is the .spec extract; second would be the LLM criteria call.
            // With a direct output block, the second call should never happen.
            // We make the second response invalid JSON to confirm it is not used.
            await MakeQueuedPass(ValidSpec, "NOT JSON").ExecuteAsync(ctx);
            Assert.Equal(3, ctx.AuthorialAssertions.Count);
            Assert.Equal("3",  ctx.AuthorialAssertions[0].Expected);
            Assert.Equal("11", ctx.AuthorialAssertions[1].Expected);
            Assert.Equal("12", ctx.AuthorialAssertions[2].Expected);
        }
        finally
        {
            File.Delete(tmp);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_FallsBackToLlmCriteria_WhenNoOutputBlock()
    {
        var (ctx, dir) = await RunQueuedPassAsync(ValidSpec, ValidCriteriaJson);
        try
        {
            // No "## Expected Output" block in SampleMarkdown → LLM criteria path runs.
            Assert.Equal(100, ctx.AuthorialAssertions.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public static void EvaluateRules_ReturnsEmpty_WhenDtoHasNoRules()
    {
        var result = MarkdownSpecPass.EvaluateRules(new AuthorialCriteriaDto());
        Assert.Empty(result);
    }

    [Fact]
    public static void EvaluateRules_ProducesCorrectFizzBuzz()
    {
        var dto = new AuthorialCriteriaDto
        {
            LoopFrom = 1,
            LoopTo   = 15,
            Rules    =
            [
                new AuthorialRuleDto { Divisor = 15, Expected = "FizzBuzz" },
                new AuthorialRuleDto { Divisor = 3,  Expected = "Fizz"     },
                new AuthorialRuleDto { Divisor = 5,  Expected = "Buzz"     },
                new AuthorialRuleDto { IsDefault = true, Expected = "{n}"  },
            ],
        };
        var result = MarkdownSpecPass.EvaluateRules(dto);
        Assert.Equal(15, result.Count);
        Assert.Equal("1",        result[0].Expected);
        Assert.Equal("Fizz",     result[2].Expected);
        Assert.Equal("Buzz",     result[4].Expected);
        Assert.Equal("FizzBuzz", result[14].Expected);
    }
}

public class ConsistencyMissingTests
{
    private const string FizzBuzzSpec = """
        program: FizzBuzz

        loop:
          from: 1
          to: 100

        branch:
          condition: fizzbuzz
          divisor: 15
          true_output: "FizzBuzz"

        branch:
          condition: fizz
          divisor: 3
          true_output: "Fizz"

        branch:
          condition: buzz
          divisor: 5
          true_output: "Buzz"

        branch:
          condition: default
          true_output: "{n}"
        """;

    private const string CollatzSpec = """
        program: Collatz

        variable:
          name: n
          type: int
          source: stdin

        while:
          variable: n
          condition: ne
          value: 1

        branch:
          condition: even
          divisor: 2
          true_assign:
            target: n
            op: div
            left: {n}
            right: 2
        """;

    [Fact]
    public void EmptyList_WhenAllTagsMatchSpec()
    {
        var missing = MarkdownSpecPass.ConsistencyMissing(["loop", "branch"], FizzBuzzSpec);
        Assert.Empty(missing);
    }

    [Fact]
    public void WhileMissing_WhenTagPresentButKeywordAbsent()
    {
        var spec    = FizzBuzzSpec; // has loop/branch but no while:
        var missing = MarkdownSpecPass.ConsistencyMissing(["loop", "branch", "while"], spec);
        Assert.Contains("while:", missing);
    }

    [Fact]
    public void NoMissing_WhenWhileTagMatchesWhileKeyword()
    {
        var missing = MarkdownSpecPass.ConsistencyMissing(["input", "arithmetic", "while"], CollatzSpec);
        Assert.Empty(missing);
    }

    [Fact]
    public void ArithmeticMissing_WhenAssignAbsent()
    {
        var missing = MarkdownSpecPass.ConsistencyMissing(["loop", "arithmetic"], FizzBuzzSpec);
        Assert.Contains("assign:", missing);
    }

    [Fact]
    public void InputMissing_WhenSourceStdinAbsent()
    {
        var missing = MarkdownSpecPass.ConsistencyMissing(["loop", "input"], FizzBuzzSpec);
        Assert.Contains("source: stdin", missing);
    }

    [Fact]
    public void BranchMissing_WhenBranchKeywordAbsent()
    {
        var spec    = "program: Minimal\n\nloop:\n  from: 1\n  to: 10\n";
        var missing = MarkdownSpecPass.ConsistencyMissing(["loop", "branch"], spec);
        Assert.Contains("branch:", missing);
    }

    [Fact]
    public void LoopMissing_WhenLoopKeywordAbsent()
    {
        var spec    = "program: Minimal\n";
        var missing = MarkdownSpecPass.ConsistencyMissing(["loop"], spec);
        Assert.Contains("loop:", missing);
    }

    [Fact]
    public void MultipleTagsMissing_ReportedTogether()
    {
        var spec    = "program: Bare\n";
        var missing = MarkdownSpecPass.ConsistencyMissing(["loop", "branch", "while", "arithmetic", "input"], spec);
        Assert.Contains("loop:",         missing);
        Assert.Contains("branch:",       missing);
        Assert.Contains("while:",        missing);
        Assert.Contains("assign:",       missing);
        Assert.Contains("source: stdin", missing);
        Assert.Equal(5, missing.Count);
    }

    [Fact]
    public void EmptyTags_NeverMissing()
    {
        var missing = MarkdownSpecPass.ConsistencyMissing([], FizzBuzzSpec);
        Assert.Empty(missing);
    }

    [Fact]
    public void UnknownTag_Ignored()
    {
        // Tags not in the known-keyword table should produce no missing entry.
        var missing = MarkdownSpecPass.ConsistencyMissing(["loop", "unknown_future_tag"], FizzBuzzSpec);
        Assert.Empty(missing);
    }
}
