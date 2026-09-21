using System.Threading;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Thread-safe singleton counter for active SSE streaming connections
/// between calling agents (OpenCode CLI, etc.) and AI providers through
/// this gateway. Incremented when a streaming request starts, decremented
/// on completion, error, or client disconnect.
/// Fires <see cref="OnCountChanged"/> so Blazor dashboard can react in real time.
/// </summary>
public sealed class ActiveStreamCounter
{
    private int _count;

    public int ActiveCount => _count;

    /// <summary>Fired whenever the count changes (increment or decrement).</summary>
    public event Action? OnCountChanged;

    public void Increment()
    {
        Interlocked.Increment(ref _count);
        OnCountChanged?.Invoke();
    }

    public void Decrement()
    {
        Interlocked.Decrement(ref _count);
        OnCountChanged?.Invoke();
    }
}
