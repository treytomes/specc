namespace IronLlm.Graph;

public enum EdgeType
{
    Contains,
    Executes,
    Reads,
    Writes,
    TrueBranch,
    FalseBranch,
    DependsOn,
    Asserts,
}

public record Edge(Guid Id, Guid From, Guid To, EdgeType Type);
