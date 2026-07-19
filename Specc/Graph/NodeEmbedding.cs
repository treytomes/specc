namespace Specc.Graph;

/// <summary>Associates a graph node with its embedding vector produced by <see cref="Specc.Passes.EmbeddingPass"/>.</summary>
/// <param name="NodeId">Identifier of the graph node this embedding belongs to.</param>
/// <param name="NodeLabel">Human-readable label of the node, stored for diagnostics.</param>
/// <param name="Vector">Floating-point embedding vector from mxbai-embed-large.</param>
public record NodeEmbedding(Guid NodeId, string NodeLabel, float[] Vector);
