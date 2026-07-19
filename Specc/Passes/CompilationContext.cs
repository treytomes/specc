using Specc.Graph;
using Specc.Passes.Repository;

namespace Specc.Passes;

/// <summary>Shared mutable state threaded through every pass in the compilation pipeline.</summary>
public class CompilationContext
{
    /// <summary>Path to the current spec file being compiled (may be updated by <c>MarkdownSpecPass</c>).</summary>
    public required string SpecPath { get; set; }

    /// <summary>Path to the original input file, preserved when <c>SpecPath</c> is updated.</summary>
    public string? InputPath { get; init; }

    /// <summary>Directory where all pass artifacts are written.</summary>
    public required string ArtifactsDir { get; init; }

    /// <summary>Raw text content of the <c>.spec</c> file.</summary>
    public string? RawSpec { get; set; }

    /// <summary>Typed semantic graph built by <c>SemanticGraphPass</c>.</summary>
    public SemanticGraph? SemanticGraph { get; set; }

    /// <summary>Per-node embedding vectors produced by <c>EmbeddingPass</c> (and refined by <c>NodeMlpPass</c>).</summary>
    public List<NodeEmbedding> Embeddings { get; set; } = [];

    /// <summary>CFG basic blocks produced by <c>CfgPass</c>.</summary>
    public List<CfgBlock> CfgBlocks { get; set; } = [];

    /// <summary>Stack IR instructions produced by <c>StackIrPass</c>.</summary>
    public List<StackInstruction> StackIr { get; set; } = [];

    /// <summary>True after <c>SemanticNormalizationPass</c> has validated and relabelled nodes.</summary>
    public bool GraphNormalized { get; set; } = false;

    /// <summary>Graph-derived acceptance assertions produced by <c>AcceptanceCriteriaPass</c>.</summary>
    public List<AssertionRecord> Assertions { get; set; } = [];

    /// <summary>Authorial assertions extracted from the Markdown source by <c>MarkdownSpecPass</c>.</summary>
    public List<AssertionRecord> AuthorialAssertions { get; set; } = [];

    /// <summary>Semantic validation report produced by <c>SemanticValidationPass</c>.</summary>
    public ValidationReport? ValidationReport { get; set; }

    /// <summary>Textual IL output produced by <c>MsilGenerationPass</c>.</summary>
    public string? MsilOutput    { get; set; }

    /// <summary>Path to the emitted managed assembly DLL.</summary>
    public string? AssemblyPath  { get; set; }

    /// <summary>Path to the native apphost launcher executable.</summary>
    public string? LauncherPath  { get; set; }

    /// <summary>Fixed stdin content piped to the process during acceptance verification.</summary>
    public string? TestInput     { get; set; }

    /// <summary>Root directory of the graph repository used for retrieval and persistence.</summary>
    public string RepositoryPath { get; set; } = "repository";

    /// <summary>Similar prior compilations retrieved by <c>RepositoryRetrievalPass</c>.</summary>
    public List<SimilarPrior> SimilarPriors { get; set; } = [];

    /// <summary>Stdout lines captured during <c>AcceptanceVerificationPass</c>; used in tests to avoid a second process launch.</summary>
    public string[]? VerificationOutput { get; set; }

    /// <summary>Acceptance verification result: null = not yet run, true = passed, false = failed.</summary>
    public bool? AcceptancePassed { get; set; }

    /// <summary>Number of assertions that were evaluated during acceptance verification.</summary>
    public int   AssertionCount   { get; set; }
}
