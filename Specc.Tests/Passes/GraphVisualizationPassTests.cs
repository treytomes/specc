using Specc.Graph;
using Specc.Passes;
using Specc.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Specc.Tests.Passes;

public class GraphVisualizationPassTests
{
    private static GraphVisualizationPass MakePass() =>
        new(NullLogger<GraphVisualizationPass>.Instance);

    private static async Task<(CompilationContext ctx, string dir)> RunPassAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        // Build a fresh context with the temp dir as ArtifactsDir.
        var baseCtx = PipelineFixtures.AfterGraph();
        var ctx = new CompilationContext
        {
            SpecPath      = "fake.spec",
            ArtifactsDir  = dir,
            SemanticGraph = baseCtx.SemanticGraph,
        };
        await MakePass().ExecuteAsync(ctx);
        return (ctx, dir);
    }

    // ── Artifact existence ────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_WritesMmdFile()
    {
        var (_, dir) = await RunPassAsync();
        try { Assert.True(File.Exists(Path.Combine(dir, "02b-semantic-graph.mmd"))); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Execute_WritesSvgFile()
    {
        var (_, dir) = await RunPassAsync();
        try { Assert.True(File.Exists(Path.Combine(dir, "02c-semantic-graph.svg"))); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ArtifactFile_IsExpectedFilename()
    {
        Assert.Equal("02b-semantic-graph.mmd", MakePass().ArtifactFile);
    }

    // ── Mermaid content ───────────────────────────────────────────────────────

    [Fact]
    public void BuildMermaid_StartsWithFlowchartTD()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var mmd = GraphVisualizationPass.BuildMermaid(nodes, edges);
        Assert.StartsWith("flowchart TD", mmd);
    }

    [Fact]
    public void BuildMermaid_ContainsProgramNodeLabel()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var mmd = GraphVisualizationPass.BuildMermaid(nodes, edges);
        Assert.Contains("Program:FizzBuzz", mmd);
    }

    [Fact]
    public void BuildMermaid_ContainsEdgeArrows()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var mmd = GraphVisualizationPass.BuildMermaid(nodes, edges);
        Assert.Contains("-->", mmd);
    }

    [Fact]
    public void BuildMermaid_DependsOn_UsesDottedArrow()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var mmd = GraphVisualizationPass.BuildMermaid(nodes, edges);
        // DependsOn edges use the dotted Mermaid syntax.
        Assert.Contains("DependsOn", mmd);
        Assert.Contains("-.", mmd);
    }

    [Fact]
    public void BuildMermaid_ExcludesAssertionNodes()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var assertionNode = new AssertionNode(Guid.NewGuid(), "Assert:1=1", 1, "1");
        var nodesWithAssertion = nodes.Append(assertionNode).ToList();
        var mmd = GraphVisualizationPass.BuildMermaid(nodesWithAssertion, edges);
        Assert.DoesNotContain("Assert:1=1", mmd);
    }

    // ── SVG content ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildSvg_IsWellFormedXml()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var svg = GraphVisualizationPass.BuildSvg(nodes, edges);
        // If the XML is malformed this throws.
        var _ = System.Xml.Linq.XDocument.Parse(svg);
    }

    [Fact]
    public void BuildSvg_HasSvgRootElement()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var svg = GraphVisualizationPass.BuildSvg(nodes, edges);
        Assert.StartsWith("<svg", svg.TrimStart());
    }

    [Fact]
    public void BuildSvg_ContainsRectForEachProgramNode()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var svg = GraphVisualizationPass.BuildSvg(nodes, edges);
        Assert.Contains("<rect", svg);
    }

    [Fact]
    public void BuildSvg_ContainsProgramNodeColor()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var svg = GraphVisualizationPass.BuildSvg(nodes, edges);
        Assert.Contains("#4a90d9", svg);   // ProgramNode fill
    }

    [Fact]
    public void BuildSvg_ContainsArrowMarker()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var svg = GraphVisualizationPass.BuildSvg(nodes, edges);
        Assert.Contains("marker", svg);
    }

    [Fact]
    public void BuildSvg_ExcludesAssertionNodes()
    {
        var (nodes, edges) = FizzBuzzGraph();
        var assertionNode = new AssertionNode(Guid.NewGuid(), "Assert:99=Buzz", 99, "Buzz");
        var nodesWithAssertion = nodes.Append(assertionNode).ToList();
        var svg = GraphVisualizationPass.BuildSvg(nodesWithAssertion, edges);
        Assert.DoesNotContain("Assert:99=Buzz", svg);
    }

    [Fact]
    public void BuildSvg_EscapesXmlSpecialChars()
    {
        var nodes = new List<Node>
        {
            new ProgramNode(Guid.NewGuid(), "Program:A&B", "A&B"),
        };
        var svg = GraphVisualizationPass.BuildSvg(nodes, []);
        Assert.Contains("&amp;", svg);
        Assert.DoesNotContain("A&B\"", svg);
    }

    // ── New node type colors ──────────────────────────────────────────────────

    [Fact]
    public void BuildSvg_ArrayNode_HasAmberColor()
    {
        var id   = Guid.NewGuid();
        var node = new ArrayNode(id, "Array:arr[10]", "arr", "int", 10);
        var svg  = GraphVisualizationPass.BuildSvg([node], []);
        Assert.Contains("#d4a843", svg);
    }

    [Fact]
    public void BuildSvg_IndexNode_HasLavenderColor()
    {
        var id   = Guid.NewGuid();
        var node = new IndexNode(id, "Index:arr[i]", "arr", "i");
        var svg  = GraphVisualizationPass.BuildSvg([node], []);
        Assert.Contains("#c4a4e0", svg);
    }

    [Fact]
    public void BuildSvg_SwapNode_HasSalmonColor()
    {
        var id   = Guid.NewGuid();
        var node = new SwapNode(id, "Swap:arr[j↔j+1]", "arr", "j", "j+1");
        var svg  = GraphVisualizationPass.BuildSvg([node], []);
        Assert.Contains("#e08080", svg);
    }

    [Fact]
    public void BuildSvg_NestedLoopNode_HasTealColor()
    {
        var id   = Guid.NewGuid();
        var node = new NestedLoopNode(id, "NestedLoop:j<n-1", "j", 0, "n-1");
        var svg  = GraphVisualizationPass.BuildSvg([node], []);
        Assert.Contains("#7ab8c4", svg);
    }

    // ── LoadFromArtifact is a no-op ───────────────────────────────────────────

    [Fact]
    public async Task LoadFromArtifact_IsNoOp()
    {
        var ctx = PipelineFixtures.MakeContext();
        await MakePass().LoadFromArtifactAsync("irrelevant.mmd", ctx);
        // SemanticGraph was not set by us, so it should still be null.
        Assert.Null(ctx.SemanticGraph);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (List<Node> nodes, List<Edge> edges) FizzBuzzGraph()
    {
        var ctx = PipelineFixtures.AfterGraph();
        var graph = ctx.SemanticGraph!;
        var nodes = graph.Nodes.Where(n => n is not AssertionNode).ToList();
        var nodeIds = nodes.Select(n => n.Id).ToHashSet();
        var edges = graph.Edges
            .Where(e => nodeIds.Contains(e.From) && nodeIds.Contains(e.To))
            .ToList();
        return (nodes, edges);
    }
}
