namespace IronLlm.Passes.Repository;

public record CompiledUnit(
    Guid   Id,
    string SpecHash,
    string ProgramName,
    string CompiledAt,       // ISO 8601
    string SemanticGraphPath,
    string EmbeddingsPath,
    string CfgPath,
    string StackIrPath,
    string MsilPath,
    string SpecText = ""     // raw .spec content; empty for entries persisted before this field was added
);

public record RepositoryIndex(List<CompiledUnit> Units);

public record SimilarPrior(
    Guid   UnitId,
    string ProgramName,
    Guid   NodeId,
    string NodeLabel,
    float  Similarity
);
