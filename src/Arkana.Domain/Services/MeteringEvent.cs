using Arkana.Domain.ValueObjects;

namespace Arkana.Domain.Services;

/// <summary>
/// Discriminated union for events flowing through the batched metering
/// queue (PERF-ARKANA-005). One queue carries both token-usage events and
/// full request-log events, drained together so a single bulk INSERT
/// covers both tables in the same SaveChanges round-trip when possible.
///
/// Why one queue, not two? Chat completions always emit both events
/// (one <see cref="TokenUsage"/>, one <see cref="RequestLog"/>) on the
/// same response. Co-locating them avoids the head-of-line blocking
/// that two separate queues would introduce, and lets the flusher
/// decide the optimal batch composition.
/// </summary>
public abstract record MeteringEvent
{
    private MeteringEvent() { }

    /// <summary>Token-usage summary for billing/dashboard aggregation.</summary>
    public sealed record Usage : MeteringEvent
    {
        public TokenUsage Value { get; }
        public Usage(TokenUsage value) { Value = value; }
    }

    /// <summary>Full request/response log (used by Logs dashboard page).</summary>
    public sealed record LogEntry : MeteringEvent
    {
        public RequestLog Value { get; }
        public LogEntry(RequestLog value) { Value = value; }
    }
}
