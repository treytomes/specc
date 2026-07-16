using IronLlm.Passes;
using IronLlm.Tests.Fixtures;

namespace IronLlm.Tests.Passes;

public class MsilGenerationPassTests
{
    private static async Task<CompilationContext> BuildContextAsync()
    {
        var ctx = PipelineFixtures.AfterStackIr();
        await new MsilGenerationPass().ExecuteAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task MsilOutput_IsNotNullOrEmpty()
    {
        var ctx = await BuildContextAsync();
        Assert.NotNull(ctx.MsilOutput);
        Assert.NotEmpty(ctx.MsilOutput);
    }

    [Fact]
    public async Task MsilOutput_ContainsAssemblyDeclaration()
    {
        var il = (await BuildContextAsync()).MsilOutput!;
        Assert.Contains(".assembly FizzBuzz", il);
    }

    [Fact]
    public async Task MsilOutput_ContainsEntrypoint()
    {
        var il = (await BuildContextAsync()).MsilOutput!;
        Assert.Contains(".entrypoint", il);
    }

    [Fact]
    public async Task MsilOutput_ContainsWriteLineStringCall()
    {
        var il = (await BuildContextAsync()).MsilOutput!;
        Assert.Contains("call void [mscorlib]System.Console::WriteLine(string)", il);
    }

    [Fact]
    public async Task MsilOutput_ContainsWriteLineIntCall()
    {
        var il = (await BuildContextAsync()).MsilOutput!;
        Assert.Contains("call void [mscorlib]System.Console::WriteLine(int32)", il);
    }

    [Fact]
    public async Task MsilOutput_ContainsRet()
    {
        var il = (await BuildContextAsync()).MsilOutput!;
        Assert.Contains("ret", il);
    }

    [Fact]
    public async Task MsilOutput_ContainsLabelForEachCfgBlock()
    {
        var cfgCtx = PipelineFixtures.AfterCfg();
        var labels = cfgCtx.CfgBlocks.Select(b => b.Label).ToList();

        var stackCtx = PipelineFixtures.AfterStackIr();
        await new MsilGenerationPass().ExecuteAsync(stackCtx);
        var il = stackCtx.MsilOutput!;

        foreach (var label in labels)
            Assert.Contains($"  {label}:", il);
    }

    [Fact]
    public async Task MsilOutput_ContainsFizzBuzzStringLiteral()
    {
        var il = (await BuildContextAsync()).MsilOutput!;
        Assert.Contains("\"FizzBuzz\"", il);
    }

    [Fact]
    public async Task MsilOutput_ContainsLocalsDeclaration()
    {
        var il = (await BuildContextAsync()).MsilOutput!;
        Assert.Contains(".locals init", il);
    }

    [Fact]
    public async Task Execute_Throws_WhenStackIrEmpty()
    {
        var ctx = PipelineFixtures.MakeContext();
        await Assert.ThrowsAnyAsync<Exception>(() => new MsilGenerationPass().ExecuteAsync(ctx));
    }

    [Fact]
    public async Task LoadFromArtifact_RestoresMsilOutput()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, ".assembly Test {}");
            var ctx = PipelineFixtures.MakeContext();
            await new MsilGenerationPass().LoadFromArtifactAsync(tmp, ctx);
            Assert.Equal(".assembly Test {}", ctx.MsilOutput);
        }
        finally { File.Delete(tmp); }
    }
}
