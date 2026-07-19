namespace Specc.Passes.Repository;

/// <summary>A single compilation unit stored in the graph repository.</summary>
/// <param name="Id">Unique identifier for this compilation record.</param>
/// <param name="SpecHash">SHA-256 hash of the source spec file.</param>
/// <param name="ProgramName">Name of the compiled program.</param>
/// <param name="CompiledAt">ISO 8601 timestamp of when this unit was compiled.</param>
/// <param name="SemanticGraphPath">Absolute path to the persisted semantic graph artifact.</param>
/// <param name="EmbeddingsPath">Absolute path to the persisted embeddings artifact.</param>
/// <param name="CfgPath">Absolute path to the persisted CFG artifact.</param>
/// <param name="StackIrPath">Absolute path to the persisted stack IR artifact.</param>
/// <param name="MsilPath">Absolute path to the persisted MSIL artifact.</param>
/// <param name="SpecText">Raw <c>.spec</c> content; empty for units persisted before this field was added.</param>
/// <param name="AcceptancePassed">True when all assertions passed; false when they failed or did not run.</param>
/// <param name="AssertionCount">Number of assertions evaluated; 0 means unverified.</param>
public record CompiledUnit(
    Guid   Id,
    string SpecHash,
    string ProgramName,
    string CompiledAt,
    string SemanticGraphPath,
    string EmbeddingsPath,
    string CfgPath,
    string StackIrPath,
    string MsilPath,
    string SpecText      = "",
    bool   AcceptancePassed = false,
    int    AssertionCount   = 0
);

/// <summary>The root document of the repository index file, listing all compiled units.</summary>
/// <param name="Units">All compilation units indexed in this repository.</param>
public record RepositoryIndex(List<CompiledUnit> Units);

/// <summary>A prior compilation node whose embedding is similar to a node in the current compilation.</summary>
/// <param name="UnitId">Identifier of the prior compilation unit.</param>
/// <param name="ProgramName">Name of the prior program.</param>
/// <param name="NodeId">Identifier of the matching node in the prior compilation.</param>
/// <param name="NodeLabel">Label of the matching node in the prior compilation.</param>
/// <param name="Similarity">Cosine similarity between the prior node and the current node.</param>
public record SimilarPrior(
    Guid   UnitId,
    string ProgramName,
    Guid   NodeId,
    string NodeLabel,
    float  Similarity
);
