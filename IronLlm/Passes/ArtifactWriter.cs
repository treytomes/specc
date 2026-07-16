using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

[ExcludeFromCodeCoverage(Justification = "Filesystem I/O dispatch; covered by scripts/test.sh")]
public static class ArtifactWriter
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WritePassArtifactAsync(
        ICompilerPass pass, CompilationContext ctx, ILogger logger)
    {
        Directory.CreateDirectory(ctx.ArtifactsDir);

        switch (pass)
        {
            case MarkdownSpecPass:
                // Artifact already written inside ExecuteAsync (plain text, not JSON).
                if (pass.ArtifactFile is { } mdArt)
                    logger.LogDebug("Artifact written: {Path}", Path.Combine(ctx.ArtifactsDir, mdArt));
                break;

            case ParseSpecPass:
                await WriteJson("01-spec.json", new { raw = ctx.RawSpec }, ctx, logger);
                break;

            case SemanticGraphPass:
                await WriteJson("02-semantic-graph.json", new
                {
                    nodes = ctx.SemanticGraph?.Nodes,
                    edges = ctx.SemanticGraph?.Edges,
                }, ctx, logger);
                break;

            case EmbeddingPass:
                await WriteJson("03-embeddings.json", ctx.Embeddings.Select(e => new
                {
                    nodeId     = e.NodeId,
                    label      = e.NodeLabel,
                    dimensions = e.Vector.Length,
                    vector     = e.Vector,
                }), ctx, logger);
                break;

            case CfgPass:
                await WriteJson("04-cfg.json", ctx.CfgBlocks, ctx, logger);
                break;

            case StackIrPass:
                await WriteJson("05-stackir.json", ctx.StackIr, ctx, logger);
                break;

            case MsilGenerationPass:
                if (ctx.MsilOutput != null)
                {
                    var path = Path.Combine(ctx.ArtifactsDir, "06-program.il");
                    await File.WriteAllTextAsync(path, ctx.MsilOutput);
                    logger.LogDebug("Artifact written: {Path}", path);
                }
                break;

            case AssemblyEmitPass:
                if (ctx.AssemblyPath != null) logger.LogDebug("Artifact written: {Path}", ctx.AssemblyPath);
                if (ctx.LauncherPath != null) logger.LogDebug("Artifact written: {Path}", ctx.LauncherPath);
                break;
        }
    }

    private static async Task WriteJson(
        string filename, object? data, CompilationContext ctx, ILogger logger)
    {
        var path = Path.Combine(ctx.ArtifactsDir, filename);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, Opts));
        logger.LogDebug("Artifact written: {Path}", path);
    }
}
