namespace Arkana.Domain.Services;

/// <summary>
/// Budget enforcement with the reservation pattern (ENT-ARKANA-003).
/// Prevents a tenant from spending beyond their monthly token allowance,
/// even under high concurrency.
///
/// The plan's included tokens are the default budget. Purchased top-ups
/// increase the cap.
/// </summary>
public interface IBudgetEnforcer
{
    /// <summary>Master toggle — budget enforcement is opt-in via config.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Reserve estimated tokens from the tenant's budget.
    /// Returns true if the reservation succeeded (budget had room).
    /// Returns false if the budget is exhausted.
    /// Throws an exception on concurrency conflict (caller should retry).
    /// </summary>
    /// <param name="tenantId">The tenant whose budget to charge.</param>
    /// <param name="estimatedInput">Estimated input tokens (0 if unknown).</param>
    /// <param name="estimatedOutput">Estimated output tokens (use max_tokens or default).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> TryReserveAsync(Guid tenantId, long estimatedInput, long estimatedOutput,
        CancellationToken ct = default);

    /// <summary>
    /// Release over-reserved tokens after the actual usage is known.
    /// Called unconditionally after every request that reserved.
    /// </summary>
    Task ReleaseAsync(Guid tenantId, long reservedInput, long reservedOutput,
        long actualInput, long actualOutput, CancellationToken ct = default);
}
