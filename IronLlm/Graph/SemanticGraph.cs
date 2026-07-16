namespace IronLlm.Graph;

public class SemanticGraph
{
    public List<Node> Nodes { get; init; } = [];
    public List<Edge> Edges { get; init; } = [];

    public void Add(Node node) => Nodes.Add(node);

    public void Connect(Guid from, Guid to, EdgeType type) =>
        Edges.Add(new Edge(Guid.NewGuid(), from, to, type));

    public IEnumerable<Node> Children(Guid parentId) =>
        Edges
            .Where(e => e.From == parentId && e.Type == EdgeType.Contains)
            .Select(e => Nodes.First(n => n.Id == e.To));
}
