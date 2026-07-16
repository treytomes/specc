using IronLlm.Graph;
using IronLlm.Passes;
using IronLlm.Tests.Fixtures;
using System.Text.Json;

namespace IronLlm.Tests.Passes;

public class SemanticGraphPassLoadTests
{
    [Fact]
    public async Task LoadFromArtifact_RestoresNodes()
    {
        var ctx     = PipelineFixtures.AfterGraph();
        var graph   = ctx.SemanticGraph!;
        var opts    = new JsonSerializerOptions { WriteIndented = true };
        var json    = JsonSerializer.Serialize(new { nodes = graph.Nodes, edges = graph.Edges }, opts);

        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, json);
            var loaded = PipelineFixtures.MakeContext();
            await new SemanticGraphPass().LoadFromArtifactAsync(tmp, loaded);
            Assert.NotNull(loaded.SemanticGraph);
            Assert.Equal(graph.Nodes.Count, loaded.SemanticGraph.Nodes.Count);
            Assert.Equal(graph.Edges.Count, loaded.SemanticGraph.Edges.Count);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task LoadFromArtifact_ContainsProgramNode()
    {
        var ctx     = PipelineFixtures.AfterGraph();
        var graph   = ctx.SemanticGraph!;
        var opts    = new JsonSerializerOptions { WriteIndented = true };
        var json    = JsonSerializer.Serialize(new { nodes = graph.Nodes, edges = graph.Edges }, opts);

        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, json);
            var loaded = PipelineFixtures.MakeContext();
            await new SemanticGraphPass().LoadFromArtifactAsync(tmp, loaded);
            Assert.Single(loaded.SemanticGraph!.Nodes.OfType<ProgramNode>());
        }
        finally { File.Delete(tmp); }
    }
}
