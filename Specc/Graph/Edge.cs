namespace Specc.Graph;

/// <summary>Classifies the semantic relationship between two graph nodes.</summary>
public enum EdgeType
{
    /// <summary>Parent node structurally contains the child node.</summary>
    Contains,
    /// <summary>A control-flow execution edge from one block to the next.</summary>
    Executes,
    /// <summary>A node reads the value of another node.</summary>
    Reads,
    /// <summary>A node writes to another node.</summary>
    Writes,
    /// <summary>The true (taken) path of a conditional branch.</summary>
    TrueBranch,
    /// <summary>The false (not-taken) path of a conditional branch.</summary>
    FalseBranch,
    /// <summary>A node depends on the result of another node.</summary>
    DependsOn,
    /// <summary>An assertion node attached to the program node for acceptance verification.</summary>
    Asserts,
}

/// <summary>A directed, typed edge in the semantic graph.</summary>
/// <param name="Id">Unique edge identifier.</param>
/// <param name="From">Source node identifier.</param>
/// <param name="To">Target node identifier.</param>
/// <param name="Type">Semantic relationship expressed by this edge.</param>
public record Edge(Guid Id, Guid From, Guid To, EdgeType Type);
