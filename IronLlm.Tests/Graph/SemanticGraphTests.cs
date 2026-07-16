using IronLlm.Graph;

namespace IronLlm.Tests.Graph;

public class SemanticGraphTests
{
    [Fact]
    public void Children_ReturnsDirectContainsChildren()
    {
        var graph  = new SemanticGraph();
        var parent = new ProgramNode(Guid.NewGuid(), "prog", "MyProg");
        var child1 = new LoopNode(Guid.NewGuid(), "loop", 1, 10);
        var child2 = new VariableNode(Guid.NewGuid(), "var", "x", "int");

        graph.Add(parent);
        graph.Add(child1);
        graph.Add(child2);
        graph.Connect(parent.Id, child1.Id, EdgeType.Contains);
        graph.Connect(parent.Id, child2.Id, EdgeType.Contains);

        var children = graph.Children(parent.Id).ToList();
        Assert.Equal(2, children.Count);
        Assert.Contains(child1, children);
        Assert.Contains(child2, children);
    }

    [Fact]
    public void Children_DoesNotReturn_NonContainsEdges()
    {
        var graph  = new SemanticGraph();
        var branch = new BranchNode(Guid.NewGuid(), "b", "cond");
        var modulo = new ModuloNode(Guid.NewGuid(), "m", 3);
        graph.Add(branch);
        graph.Add(modulo);
        graph.Connect(branch.Id, modulo.Id, EdgeType.DependsOn);

        var children = graph.Children(branch.Id).ToList();
        Assert.Empty(children);
    }

    [Fact]
    public void Children_ReturnsEmpty_WhenNoEdges()
    {
        var graph = new SemanticGraph();
        var node  = new ProgramNode(Guid.NewGuid(), "prog", "P");
        graph.Add(node);
        Assert.Empty(graph.Children(node.Id));
    }

    [Fact]
    public void Add_IncreasesNodeCount()
    {
        var graph = new SemanticGraph();
        graph.Add(new ConstantNode(Guid.NewGuid(), "c", 42));
        graph.Add(new ComparisonNode(Guid.NewGuid(), "cmp", "=="));
        Assert.Equal(2, graph.Nodes.Count);
    }

    [Fact]
    public void Connect_IncreasesEdgeCount()
    {
        var graph = new SemanticGraph();
        var a = new ProgramNode(Guid.NewGuid(), "a", "A");
        var b = new LoopNode(Guid.NewGuid(), "b", 0, 5);
        graph.Add(a);
        graph.Add(b);
        graph.Connect(a.Id, b.Id, EdgeType.Executes);
        Assert.Single(graph.Edges);
    }
}
