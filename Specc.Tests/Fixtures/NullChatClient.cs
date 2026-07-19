using Microsoft.Extensions.AI;

namespace Specc.Tests.Fixtures;

// Satisfies the required CompilationContext.ChatClient without a live model.
internal sealed class NullChatClient : IChatClient
{
    public static readonly NullChatClient Instance = new();

    public ChatClientMetadata Metadata => new("null", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NullChatClient is a test stub and must not be called.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NullChatClient is a test stub and must not be called.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
