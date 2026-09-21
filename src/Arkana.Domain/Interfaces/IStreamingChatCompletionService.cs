namespace Arkana.Domain.Interfaces;

/// <summary>
/// Optional streaming contract for connectors that can expose a provider stream
/// without routing through the one-shot completion path.
/// </summary>
public interface IStreamingChatCompletionService
{
    string ProviderName { get; }
    Task<ChatStreamResult> CompleteStreamingAsync(ChatRequest request, CancellationToken ct = default);
}

/// <summary>
/// Owns a provider response stream and its completion callback. Callers must
/// mark a stream complete only after the provider's terminal marker has been
/// consumed; cancellation, parse errors, and premature EOF remain failures.
/// </summary>
public sealed class ChatStreamResult : IAsyncDisposable
{
    private readonly Func<bool, ValueTask>? _disposeAsync;
    private int _disposed;
    private bool _completed;

    public ChatStreamResult(Stream? stream, ChatResult result, Func<bool, ValueTask>? disposeAsync = null)
    {
        Stream = stream;
        Result = result;
        _disposeAsync = disposeAsync;
    }

    public Stream? Stream { get; }
    public ChatResult Result { get; }
    public bool IsSuccess => Stream is not null && Result.IsSuccess;

    /// <summary>Claims successful terminal ownership after the provider stream is complete.</summary>
    public void MarkCompleted() => _completed = true;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_disposeAsync is not null)
        {
            await _disposeAsync(_completed);
            return;
        }

        if (Stream is not null)
            await Stream.DisposeAsync();
    }
}
