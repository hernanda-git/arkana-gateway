using System.Diagnostics.Metrics;
using Arkana.Domain.Services;
using Arkana.Infrastructure.Services;

namespace Arkana.Infrastructure.Observability;

/// <summary>
/// Custom application metrics for the AI Gateway (OBS-ARKANA-002, task #20).
///
/// Exposes:
///   - <c>arkana.chat.latency</c> — Histogram (ms) tagged by provider, model, status
///   - <c>arkana.chat.requests_total</c> — Counter tagged by provider, model, status
///   - <c>arkana.chat.tokens_total</c> — Counter tagged by provider, model, direction
///   - <c>arkana.cache.hit_ratio</c> — ObservableGauge (0.0–1.0) exact-match ratio
///   - <c>arkana.cache.semantic_hit_ratio</c> — ObservableGauge (0.0–1.0) semantic ratio
///
/// Also implements <see cref="IChatMetricsRecorder"/> for injection into
/// the Application layer's chat handler.
///
/// Design:
///   Histograms and Counters are recorded inline (per-request) by the
///   <c>SendChatHandler</c>. ObservableGauges read cache counters lazily
///   at scrape time — no background flush service needed.
///
/// Meter registration:
///   The meter "Arkana.Chat" MUST be added to the OTel pipeline in
///   <c>ServiceDefaults.Extensions.cs</c> via <c>.AddMeter("Arkana.Chat")</c>.
/// </summary>
public sealed class ChatMetricsExporter : IChatMetricsRecorder, IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> hosting chat-domain metrics.</summary>
    public const string MeterName = "Arkana.Chat";

    private readonly Meter _meter;
    private readonly InMemoryResponseCache _exactMatchCache;
    private readonly ISemanticCache _semanticCache;

    // Histogram — latency in milliseconds.
    private readonly Histogram<double> _latencyHistogram;

    // Counters (absolute, recorded per-request).
    private readonly Counter<long> _requestsTotal;
    private readonly Counter<long> _tokensTotal;

    public ChatMetricsExporter(
        InMemoryResponseCache exactMatchCache,
        ISemanticCache semanticCache)
    {
        _exactMatchCache = exactMatchCache;
        _semanticCache = semanticCache;
        _meter = new Meter(MeterName, "1.0.0");

        // ── Latency histogram (p50/p95/p99) ──────────────────────
        // Bucket boundaries chosen for chat completions: 50ms to 30s.
        // Exporters compute quantiles from bucket counters automatically.
        _latencyHistogram = _meter.CreateHistogram<double>(
            name: "arkana.chat.latency",
            unit: "ms",
            description: "Chat request latency in milliseconds, by provider and model.",
            advice: new InstrumentAdvice<double>
            {
                // Explicit bucket boundaries for Prometheus-style exporters.
                HistogramBucketBoundaries = [50, 100, 200, 500, 1000, 2000, 5000, 10000, 30000],
            });

        // ── Request counter ──────────────────────────────────────
        _requestsTotal = _meter.CreateCounter<long>(
            name: "arkana.chat.requests_total",
            unit: "requests",
            description: "Total chat requests, tagged by provider, model, and success/failure status.");

        // ── Token counter ────────────────────────────────────────
        _tokensTotal = _meter.CreateCounter<long>(
            name: "arkana.chat.tokens_total",
            unit: "tokens",
            description: "Total tokens processed, tagged by direction (input|output).");

        // ── Exact-match cache hit ratio ──────────────────────────
        _meter.CreateObservableGauge<double>(
            name: "arkana.cache.hit_ratio",
            observeValue: () =>
            {
                var hits = _exactMatchCache.Hits;
                var total = hits + _exactMatchCache.Misses;
                return total > 0 ? (double)hits / total : 1.0;
            },
            unit: "ratio",
            description: "Exact-match response cache hit ratio (0.0–1.0).");

        // ── Semantic cache hit ratio ─────────────────────────────
        _meter.CreateObservableGauge<double>(
            name: "arkana.cache.semantic_hit_ratio",
            observeValue: () =>
            {
                var hits = _semanticCache.Hits;
                var total = hits + _semanticCache.Misses;
                return total > 0 ? (double)hits / total : 1.0;
            },
            unit: "ratio",
            description: "Semantic (vector) cache hit ratio (0.0–1.0).");
    }

    /// <summary>
    /// Record latency for a chat completion.
    /// </summary>
    public void RecordLatency(long elapsedMs, string provider, string model, bool isError)
    {
        _latencyHistogram.Record(
            elapsedMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("status", isError ? "error" : "success"));
    }

    /// <summary>
    /// Record a completed chat request (counts both success and error).
    /// </summary>
    public void RecordRequest(string provider, string model, bool isError)
    {
        _requestsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("status", isError ? "error" : "success"));
    }

    /// <summary>
    /// Record token consumption.
    /// </summary>
    public void RecordTokenUsage(string provider, string model, int inputTokens, int outputTokens)
    {
        if (inputTokens > 0)
        {
            _tokensTotal.Add(inputTokens,
                new KeyValuePair<string, object?>("provider", provider),
                new KeyValuePair<string, object?>("model", model),
                new KeyValuePair<string, object?>("direction", "input"));
        }

        if (outputTokens > 0)
        {
            _tokensTotal.Add(outputTokens,
                new KeyValuePair<string, object?>("provider", provider),
                new KeyValuePair<string, object?>("model", model),
                new KeyValuePair<string, object?>("direction", "output"));
        }
    }

    public void Dispose() => _meter.Dispose();
}
