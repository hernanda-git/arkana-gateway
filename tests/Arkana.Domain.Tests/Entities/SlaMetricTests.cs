using Arkana.Domain.Entities;
using FluentAssertions;

namespace Arkana.Domain.Tests.Entities;

public sealed class SlaMetricTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var tenantId = Guid.NewGuid();
        var metric = SlaMetric.Create("openai", "gpt-4o", tenantId);

        metric.Id.Should().NotBe(Guid.Empty);
        metric.ProviderCode.Should().Be("openai");
        metric.ModelCode.Should().Be("gpt-4o");
        metric.TenantId.Should().Be(tenantId);
        metric.UptimePercent.Should().Be(100.0);
        metric.IsHealthy.Should().BeTrue();
        metric.TotalRequests.Should().Be(0);
    }

    [Fact]
    public void RecordSuccess_UpdatesMetrics()
    {
        var metric = SlaMetric.Create("openai", "gpt-4o", Guid.NewGuid());

        metric.RecordSuccess(150.0);

        metric.TotalRequests.Should().Be(1);
        metric.SuccessfulRequests.Should().Be(1);
        metric.FailedRequests.Should().Be(0);
        metric.AvgLatencyMs.Should().Be(150.0);
        metric.ErrorRate.Should().Be(0);
        metric.ConsecutiveFailures.Should().Be(0);
        metric.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_UpdatesMetrics()
    {
        var metric = SlaMetric.Create("openai", "gpt-4o", Guid.NewGuid());

        metric.RecordFailure(5000.0);

        metric.TotalRequests.Should().Be(1);
        metric.FailedRequests.Should().Be(1);
        metric.ConsecutiveFailures.Should().Be(1);
        metric.ErrorRate.Should().Be(1.0);
        metric.LastFailureAt.Should().NotBeNull();
    }

    [Fact]
    public void RecordFailure_BecomesUnhealthyAfterThreshold()
    {
        var metric = SlaMetric.Create("openai", "gpt-4o", Guid.NewGuid());

        // 5 consecutive failures = unhealthy
        for (int i = 0; i < 5; i++)
            metric.RecordFailure(100.0);

        metric.IsHealthy.Should().BeFalse();
        metric.ConsecutiveFailures.Should().Be(5);
    }

    [Fact]
    public void RecordSuccess_ResetsConsecutiveFailures()
    {
        var metric = SlaMetric.Create("openai", "gpt-4o", Guid.NewGuid());

        metric.RecordFailure(100.0);
        metric.RecordFailure(100.0);
        metric.RecordSuccess(100.0);

        metric.ConsecutiveFailures.Should().Be(0);
        // Error rate is still 67% (2/3), so not healthy yet
        metric.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public void EnoughSuccesses_RecoverHealth()
    {
        var metric = SlaMetric.Create("openai", "gpt-4o", Guid.NewGuid());

        metric.RecordFailure(100.0);
        metric.RecordFailure(100.0);
        // 20 successes to bring error rate to 2/22 ≈ 9%, below 10% threshold
        for (int i = 0; i < 20; i++) metric.RecordSuccess(100.0);

        metric.ConsecutiveFailures.Should().Be(0);
        metric.ErrorRate.Should().BeLessThan(0.1);
        metric.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void HighErrorRate_BecomesUnhealthy()
    {
        var metric = SlaMetric.Create("openai", "gpt-4o", Guid.NewGuid());

        // 3 successes, 8 failures = 72% error rate
        for (int i = 0; i < 3; i++) metric.RecordSuccess(100.0);
        for (int i = 0; i < 8; i++) metric.RecordFailure(100.0);

        metric.ErrorRate.Should().BeGreaterThan(0.1);
        metric.IsHealthy.Should().BeFalse();
    }
}
