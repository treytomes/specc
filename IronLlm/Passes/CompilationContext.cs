using IronLlm.Graph;
using Microsoft.Extensions.AI;

namespace IronLlm.Passes;

public class CompilationContext
{
    public required string SpecPath { get; init; }
    public required string ArtifactsDir { get; init; }
    public required IEmbeddingGenerator<string, Embedding<float>> Embedder { get; init; }
    public required IChatClient ChatClient { get; init; }

    public string? RawSpec { get; set; }
    public SemanticGraph? SemanticGraph { get; set; }
    public List<NodeEmbedding> Embeddings { get; set; } = [];
    public List<CfgBlock> CfgBlocks { get; set; } = [];
    public List<StackInstruction> StackIr { get; set; } = [];
    public string? MsilOutput    { get; set; }
    public string? AssemblyPath  { get; set; }
    public string? LauncherPath  { get; set; }
}
