namespace Specc.Passes.Repository;

public record CompiledUnit(
    Guid   Id,
    string SpecHash,
    string ProgramName,
    string CompiledAt,        // ISO 8601
    string SemanticGraphPath,
    string EmbeddingsPath,
    string CfgPath,
    string StackIrPath,
    string MsilPath,
    string SpecText      = "", // raw .spec content; empty for entries persisted before this field was added
    bool   AcceptancePassed = false, // true = all assertions passed; false = failed or not run
    int    AssertionCount   = 0      // 0 = unverified (no assertions available)
);

public record RepositoryIndex(List<CompiledUnit> Units);

public record SimilarPrior(
    Guid   UnitId,
    string ProgramName,
    Guid   NodeId,
    string NodeLabel,
    float  Similarity
);
