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
    // Fixed stdin input piped to the process during acceptance verification.
    // Used for interactive programs (e.g. Greetings) where output depends on user input.
    public string? TestInput     { get; set; }

    public string RepositoryPath { get; set; } = "repository";
    public List<SimilarPrior> SimilarPriors { get; set; } = [];

    // Stdout captured during AcceptanceVerificationPass; avoids a second process launch in tests.
    public string[]? VerificationOutput { get; set; }

    // Set by AcceptanceVerificationPass on completion.
    // null  = verification has not run yet (e.g. loading from artifact)
    // true  = all assertions passed (or no assertions — unverified but not wrong)
    // false = one or more assertions failed
    public bool? AcceptancePassed { get; set; }
    public int   AssertionCount   { get; set; }
}
