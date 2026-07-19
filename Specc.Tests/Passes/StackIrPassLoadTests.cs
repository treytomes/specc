using Specc.Graph;
using Specc.Tests.Fixtures;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Specc.Tests.Passes;

public class StackIrPassLoadTests
{
    [Fact]
    public async Task LoadFromArtifact_RestoresStackIr()
    {
        var ctx  = PipelineFixtures.AfterStackIr();
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = JsonSerializer.Serialize(ctx.StackIr, opts);

        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, json);
            var loaded = PipelineFixtures.MakeContext();
            await PipelineFixtures.MakeStackIrPass().LoadFromArtifactAsync(tmp, loaded);
            Assert.Equal(ctx.StackIr.Count, loaded.StackIr.Count);
            Assert.Contains(loaded.StackIr, i => i.Op == OpCode.Ret);
        }
        finally { File.Delete(tmp); }
    }
}
