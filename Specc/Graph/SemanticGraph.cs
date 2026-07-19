namespace Specc.Graph;

/// <summary>The typed node-and-edge graph representing a program's semantic structure.</summary>
public class SemanticGraph
{
    /// <summary>All nodes in the graph.</summary>
    public List<Node> Nodes { get; init; } = [];

    /// <summary>All edges in the graph.</summary>
    public List<Edge> Edges { get; init; } = [];

    /// <summary>Adds a node to the graph.</summary>
    public void Add(Node node) => Nodes.Add(node);

    /// <summary>Creates a typed directed edge between two nodes and adds it to the graph.</summary>
    public void Connect(Guid from, Guid to, EdgeType type) =>
        Edges.Add(new Edge(Guid.NewGuid(), from, to, type));

    /// <summary>Returns all nodes directly contained by the given parent node via <see cref="EdgeType.Contains"/> edges.</summary>
    public IEnumerable<Node> Children(Guid parentId) =>
        Edges
            .Where(e => e.From == parentId && e.Type == EdgeType.Contains)
            .Select(e => Nodes.First(n => n.Id == e.To));
}
