using Microsoft.Extensions.AI;

namespace Specc.Tests.Fixtures;

// Satisfies the required CompilationContext.Embedder without an Ollama connection.
// Should never be called in unit tests — EmbeddingPass is excluded from coverage.
internal sealed class NullEmbedder : IEmbeddingGenerator<string, Embedding<float>>
{
    public static readonly NullEmbedder Instance = new();

    public EmbeddingGeneratorMetadata Metadata => new("null", null, null, null);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NullEmbedder is a test stub and must not be called.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
