using IronLlm.Passes;
using IronLlm.Tests.Fixtures;

namespace IronLlm.Tests.Passes;

public class ParseSpecPassTests
{
    [Fact]
    public async Task RawSpec_IsPopulated_AfterExecution()
    {
        var spec = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(spec, PipelineFixtures.FizzBuzzSpecText);
            var ctx = PipelineFixtures.MakeContext(spec);
            await new ParseSpecPass().ExecuteAsync(ctx);
            Assert.NotNull(ctx.RawSpec);
            Assert.NotEmpty(ctx.RawSpec);
        }
        finally { File.Delete(spec); }
    }

    [Fact]
    public async Task RawSpec_ContainsProgramName()
    {
        var spec = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(spec, PipelineFixtures.FizzBuzzSpecText);
            var ctx = PipelineFixtures.MakeContext(spec);
            await new ParseSpecPass().ExecuteAsync(ctx);
            Assert.Contains("FizzBuzz", ctx.RawSpec);
        }
        finally { File.Delete(spec); }
    }

    [Fact]
    public async Task LoadFromArtifact_RestoresRawSpec()
    {
        var artifact = Path.GetTempFileName();
        try
        {
            var json = """{"raw":"program: Hello"}""";
            await File.WriteAllTextAsync(artifact, json);
            var ctx = PipelineFixtures.MakeContext();
            await new ParseSpecPass().LoadFromArtifactAsync(artifact, ctx);
            Assert.Equal("program: Hello", ctx.RawSpec);
        }
        finally { File.Delete(artifact); }
    }

    [Fact]
    public async Task Execute_Throws_WhenFileDoesNotExist()
    {
        var ctx = PipelineFixtures.MakeContext("/does/not/exist.spec");
        await Assert.ThrowsAnyAsync<Exception>(() => new ParseSpecPass().ExecuteAsync(ctx));
    }
}
