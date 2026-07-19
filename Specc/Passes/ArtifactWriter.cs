using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

/// <summary>Writes the output artifact for each pass to the artifacts directory.</summary>
[ExcludeFromCodeCoverage(Justification = "Filesystem I/O dispatch; covered by scripts/test.sh")]
public static class ArtifactWriter
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes the artifact for the given pass to the artifacts directory of the context.</summary>
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

            case NodeMlpPass:
                await WriteJson("03a-refined-embeddings.json", ctx.Embeddings.Select(e => new
                {
                    nodeId    = e.NodeId,
                    nodeLabel = e.NodeLabel,
                    vector    = e.Vector,
                }), ctx, logger);
                break;

            case CfgPass:
                await WriteJson("04-cfg.json", ctx.CfgBlocks, ctx, logger);
                break;

            case StackIrPass:
                await WriteJson("05-stackir.json", ctx.StackIr, ctx, logger);
                break;

            case SemanticValidationPass:
                // Artifact already written inside ExecuteAsync.
                if (pass.ArtifactFile is { } valArt)
                    logger.LogDebug("Artifact written: {Path}", Path.Combine(ctx.ArtifactsDir, valArt));
                break;

            case MsilGenerationPass:
                if (ctx.MsilOutput != null)
                {
                    var path = Path.Combine(ctx.ArtifactsDir, "06-program.il");
                    await File.WriteAllTextAsync(path, ctx.MsilOutput);
                    logger.LogDebug("Artifact written: {Path}", path);
                }
                break;

            case GraphVisualizationPass:
                // Both .mmd and .svg written inside ExecuteAsync.
                if (pass.ArtifactFile is { } vizArt)
                    logger.LogDebug("Artifact written: {Path}", Path.Combine(ctx.ArtifactsDir, vizArt));
                break;

            case AcceptanceCriteriaPass:
                await WriteJson("00-acceptance.json", ctx.Assertions, ctx, logger);
                break;

            case AssemblyEmitPass:
                if (ctx.AssemblyPath != null) logger.LogDebug("Artifact written: {Path}", ctx.AssemblyPath);
                if (ctx.LauncherPath != null) logger.LogDebug("Artifact written: {Path}", ctx.LauncherPath);
                break;

            case AcceptanceVerificationPass:
                // Terminal pass — no artifact.
                break;

            case RepositoryRetrievalPass:
                // No artifact — retrieval is always fresh.
                break;

            case RepositoryPersistPass:
                // No artifact — persistence is fire-and-forget.
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
