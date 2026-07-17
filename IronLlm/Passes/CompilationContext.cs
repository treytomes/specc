using IronLlm.Graph;
using IronLlm.Passes.Repository;

namespace IronLlm.Passes;

public class CompilationContext
{
    public required string SpecPath { get; set; }
    public string? InputPath { get; init; }
    public required string ArtifactsDir { get; init; }

    public string? RawSpec { get; set; }
    public SemanticGraph? SemanticGraph { get; set; }
    public List<NodeEmbedding> Embeddings { get; set; } = [];
    public List<CfgBlock> CfgBlocks { get; set; } = [];
    public List<StackInstruction> StackIr { get; set; } = [];
    public bool GraphNormalized { get; set; } = false;
    public List<AssertionRecord> Assertions { get; set; } = [];
    public List<AssertionRecord> AuthorialAssertions { get; set; } = [];

    public ValidationReport? ValidationReport { get; set; }

    public string? MsilOutput    { get; set; }
    public string? AssemblyPath  { get; set; }
    public string? LauncherPath  { get; set; }

    public string RepositoryPath { get; set; } = "repository";
    public List<SimilarPrior> SimilarPriors { get; set; } = [];
}
