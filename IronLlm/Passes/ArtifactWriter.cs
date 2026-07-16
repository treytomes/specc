using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IronLlm.Passes;

[ExcludeFromCodeCoverage(Justification = "Filesystem I/O dispatch; covered by scripts/test.sh")]
public static class ArtifactWriter
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Called immediately after each pass succeeds in the incremental pipeline.
    public static async Task WritePassArtifactAsync(ICompilerPass pass, CompilationContext ctx)
    {
        Directory.CreateDirectory(ctx.ArtifactsDir);

        switch (pass)
        {
            case ParseSpecPass:
                await WriteJson("01-spec.json", new { raw = ctx.RawSpec }, ctx);
                break;

            case SemanticGraphPass:
                await WriteJson("02-semantic-graph.json", new
                {
                    nodes = ctx.SemanticGraph?.Nodes,
                    edges = ctx.SemanticGraph?.Edges,
                }, ctx);
                break;

            case EmbeddingPass:
                await WriteJson("03-embeddings.json", ctx.Embeddings.Select(e => new
                {
                    nodeId     = e.NodeId,
                    label      = e.NodeLabel,
                    dimensions = e.Vector.Length,
                    vector     = e.Vector,
                }), ctx);
                break;

            case CfgPass:
                await WriteJson("04-cfg.json", ctx.CfgBlocks, ctx);
                break;

            case StackIrPass:
                await WriteJson("05-stackir.json", ctx.StackIr, ctx);
                break;

            case MsilGenerationPass:
                if (ctx.MsilOutput != null)
                {
                    var path = Path.Combine(ctx.ArtifactsDir, "06-program.il");
                    await File.WriteAllTextAsync(path, ctx.MsilOutput);
                    Console.WriteLine($"  → {path}");
                }
                break;

            case AssemblyEmitPass:
                if (ctx.AssemblyPath  != null) Console.WriteLine($"  → {ctx.AssemblyPath}");
                if (ctx.LauncherPath  != null) Console.WriteLine($"  → {ctx.LauncherPath}");
                break;
        }
    }

    private static async Task WriteJson(string filename, object? data, CompilationContext ctx)
    {
        var path = Path.Combine(ctx.ArtifactsDir, filename);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, Opts));
        Console.WriteLine($"  → {path}");
    }
}
