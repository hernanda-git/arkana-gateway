namespace Arkana.Domain.Services;

/// <summary>
/// Minimal metrics recorder for chat completion observability
/// (OBS-ARKANA-002). The Infrastructure layer provides an OpenTelemetry
/// implementation; this interface keeps Application-layer code free
/// of direct Infrastructure dependencies.
///
/// All methods are fire-and-forget — failures are caught and logged
/// by the implementation, never propagated to the caller.
/// </summary>
public interface IChatMetricsRecorder
{
    /// <summary>
    /// Record the latency of a chat completion.
    /// </summary>
    void RecordLatency(long elapsedMs, string provider, string model, bool isError);

    /// <summary>
    /// Count a completed chat request (success or error).
    /// </summary>
    void RecordRequest(string provider, string model, bool isError);

    /// <summary>
    /// Record token consumption for a completed request.
    /// </summary>
    void RecordTokenUsage(string provider, string model, int inputTokens, int outputTokens);
}
