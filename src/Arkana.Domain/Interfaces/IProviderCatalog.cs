using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Read-mostly, in-memory catalog of AI provider configurations.
///
/// Why this exists (PERF-ARKANA-001):
///   Every chat request currently triggers a chain of DB reads:
///
///     SendChatHandler
///      ├── _modelRepo.GetAllAsync()           ← DB hit 1
///      ├── _apiKeyRepo.GetByKeyHashAsync()    ← DB hit 2
///      ├── _providerRepo.GetAllAsync()        ← DB hit 3
///      └── IChatCompletionService.CompleteAsync
///           ├── _providerRepo.GetAllAsync()   ← DB hit 4 (OpenCode)
///           ├── _providerRepo.GetAllAsync()   ← DB hit 5 (DeepSeek, on fallback)
///           └── ...
///
///   The provider table is read on the hot path of every outbound call,
///   and the configuration changes are rare (admin actions). The catalog
///   amortizes those reads — one DB call per TTL window, regardless of
///   how many chat requests fly through.
///
/// Contract:
///   - Thread-safe; multiple concurrent readers should not serialize on a DB call.
///   - <see cref="GetAllAsync"/> returns a fresh snapshot per call (defensive copy),
///     so callers cannot mutate catalog state.
///   - <see cref="GetByCodeAsync"/> looks up a single provider by its code.
///   - <see cref="Invalidate"/> forces the next call to re-read from the underlying store.
///     Call this from any write path that modifies an <see cref="AiProvider"/>.
///   - <see cref="GetAllEnabledAsync"/> is the most common hot-path call: it
///     returns enabled providers ordered by <see cref="AiProvider.Priority"/>.
/// </summary>
public interface IProviderCatalog
{
    /// <summary>
    /// All providers, regardless of enabled state. Snapshot is fresh on each call.
    /// </summary>
    Task<IReadOnlyList<AiProvider>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Tenant-scoped provider snapshot for authenticated request routing.</summary>
    Task<IReadOnlyList<AiProvider>> GetAllAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Enabled providers ordered by <see cref="AiProvider.Priority"/> ascending.
    /// This is the workhorse of the fallback-chain executor.
    /// </summary>
    Task<IReadOnlyList<AiProvider>> GetAllEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// Look up a single provider by its stable <see cref="AiProvider.Code"/>
    /// (e.g. "opencode", "deepseek"). Returns null if not found.
    /// </summary>
    Task<AiProvider?> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Tenant-scoped provider lookup; never falls back to a global row.</summary>
    Task<AiProvider?> GetByCodeAsync(string code, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Force the next read to bypass the cache and reload from the underlying
    /// repository. Call this from admin endpoints that mutate providers.
    /// </summary>
    void Invalidate();
}
