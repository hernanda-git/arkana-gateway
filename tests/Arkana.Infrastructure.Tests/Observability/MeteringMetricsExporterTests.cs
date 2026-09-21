using System.Diagnostics.Metrics;
using Arkana.Domain.Services;
using Arkana.Infrastructure.Observability;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Observability;

/// <summary>
/// The dropped-audit-row counter is only useful if it actually leaves the process on the meter the
/// OTel pipeline listens to. These tests pin the meter name, the instrument name and the delta
/// semantics of <see cref="MeteringMetricsExporter.Flush"/>.
/// </summary>
public sealed class MeteringMetricsExporterTests
{
    private const string AuditDropped = "arkana.metering.audit_dropped";

    [Fact]
    public void Flush_reports_dropped_audit_rows_on_the_metering_meter()
    {
        using var harness = new Harness();

        RequestLogAuditMetrics.SnapshotAndResetDropped();
        RequestLogAuditMetrics.RecordDropped();
        RequestLogAuditMetrics.RecordDropped();

        harness.Exporter.Flush();

        harness.Measurements.Should().Contain(
            (AuditDropped, 2L),
            "Flush must emit the dropped-audit-row delta on the Arkana.Metering meter");
    }

    [Fact]
    public void Flush_does_not_emit_the_audit_counter_at_zero()
    {
        using var harness = new Harness();

        RequestLogAuditMetrics.SnapshotAndResetDropped();

        harness.Exporter.Flush();

        harness.Measurements.Where(m => m.Instrument == AuditDropped).Should().BeEmpty(
            "zero-valued counters are not emitted, so an idle fleet shows no audit_dropped series");
    }

    /// <summary>
    /// Owns the exporter and a <see cref="MeterListener"/> that records (instrument name, value)
    /// pairs for every instrument published on the metering meter. The listener is a field so it
    /// stays rooted for the whole test — an unrooted listener can be collected and then silently
    /// stops reporting.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly MeterListener _listener;

        public Harness()
        {
            var queue = Substitute.For<IMeteringQueue>();
            queue.SnapshotAndResetCounters().Returns(new MeteringQueueCounters());
            Exporter = new MeteringMetricsExporter(queue);

            _listener = new MeterListener();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeteringMetricsExporter.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, _, _) => Measurements.Add((instrument.Name, value)));
            _listener.Start();
        }

        public MeteringMetricsExporter Exporter { get; }

        public List<(string Instrument, long Value)> Measurements { get; } = [];

        public void Dispose()
        {
            _listener.Dispose();
            Exporter.Dispose();
        }
    }
}
