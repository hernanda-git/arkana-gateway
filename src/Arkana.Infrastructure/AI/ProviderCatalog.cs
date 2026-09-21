using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Default <see cref="IProviderCatalog"/> implementation. Caches a snapshot
/// of all providers in memory and refreshes it on a TTL or on explicit
/// <see cref="Invalidate"/> calls.
///
/// Design:
///   - One <see cref="IReadOnlyList{AiProvider}"/> snapshot is held in a
///     <see cref="ReaderWriterLockSlim"/> for read-heavy access. Readers
///     run lock-free; only invalidation and refresh take the write lock.
///   - Single-flight refresh: when the cache is stale and N callers arrive
///     concurrently, only one of them runs the DB query; the others wait
///     on a <see cref="SemaphoreSlim"/>. This prevents a thundering herd
///     at startup or after invalidation.
///   - TTL is configurable; default 30 seconds.
/// </summary>
public sealed class ProviderCatalog : IProviderCatalog, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProviderCatalog> _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private List<AiProvider> _snapshot = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Default TTL: 30 seconds. Provider config changes are rare (admin
    /// actions), but credentials can rotate without notice — short enough
    /// to pick up rotations within a window, long enough to avoid hammering
    /// the DB.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    public ProviderCatalog(
        IServiceScopeFactory scopeFactory,
        ILogger<ProviderCatalog> logger,
        TimeProvider? time = null,
        TimeSpan? ttl = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
    }

    public async Task<IReadOnlyList<AiProvider>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct);
        return Snapshot();
    }

    public async Task<IReadOnlyList<AiProvider>> GetAllAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new InvalidOperationException("Authenticated tenant is required.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAiProviderRepository>();
        return await repository.GetAllAsync(tenantId, ct);
    }

    public async Task<IReadOnlyList<AiProvider>> GetAllEnabledAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.Where(p => p.IsEnabled)
                  .OrderBy(p => p.Priority)
                  .ToList();
    }

    public async Task<AiProvider?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var all = await GetAllAsync(ct);
        return all.FirstOrDefault(p =>
            p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AiProvider?> GetByCodeAsync(string code, Guid tenantId, CancellationToken ct = default)
    {
        var all = await GetAllAsync(tenantId, ct);
        return all.FirstOrDefault(p =>
            p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
    }

    public void Invalidate()
    {
        _lock.EnterWriteLock();
        try
        {
            _loadedAt = DateTimeOffset.MinValue;
            _logger.LogDebug("Provider catalog invalidated");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// True when the snapshot is older than the configured TTL. Always
    /// returns true on a cold cache (LoadedAt == MinValue).
    /// </summary>
    private bool IsStale()
    {
        _lock.EnterReadLock();
        try
        {
            return _time.GetUtcNow() - _loadedAt > _ttl;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns a defensive copy of the current snapshot. Callers cannot
    /// mutate the catalog's internal state by mutating the returned list.
    /// </summary>
    private List<AiProvider> Snapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return _snapshot.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private async Task EnsureFreshAsync(CancellationToken ct)
    {
        if (!IsStale()) return;

        // Single-flight: only one refresh runs at a time, even if N
        // callers all saw "stale" simultaneously.
        await _refreshGate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the gate — another caller may have
            // already refreshed.
            if (!IsStale()) return;

            // The repository (and its DbContext) is registered scoped. The
            // catalog is a singleton, so resolve a fresh scope per refresh
            // instead of capturing a scoped dependency into the singleton.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAiProviderRepository>();
            var fresh = await repository.GetAllAsync(ct);

            _lock.EnterWriteLock();
            try
            {
                // Materialize to a List<AiProvider> so subsequent
                // enumerations don't re-allocate. The repository returns
                // a defensive copy, so it's safe to own.
                _snapshot = fresh.ToList();
                _loadedAt = _time.GetUtcNow();
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            _logger.LogDebug(
                "Provider catalog refreshed: {Count} providers (TTL {Ttl}s)",
                fresh.Count, (int)_ttl.TotalSeconds);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Disposes the lock and semaphore. The catalog is a long-lived
    /// singleton, so disposal only fires on app shutdown — but the
    /// analyzer correctly flags the disposable fields.
    /// </summary>
    public void Dispose()
    {
        _lock.Dispose();
        _refreshGate.Dispose();
    }
}
