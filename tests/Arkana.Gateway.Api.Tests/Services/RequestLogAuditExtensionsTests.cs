using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Arkana.Gateway.Api.Tests.Services;

/// <summary>
/// Regression coverage for the 2026-09-21 stream-reset incident: the ChatGPT/Codex
/// branch of <c>POST /v1/responses</c> called the context-free
/// <c>RecordWithContextAsync(log, ct)</c> overload, so the audit row kept
/// <see cref="Guid.Empty"/> as its tenant. <c>BatchedRequestLogger</c> rejected that row
/// with an <see cref="InvalidOperationException"/> raised from inside the endpoint, which
/// aborted the in-flight SSE response and reached clients (Kilo-Code / Bun) as
/// ECONNRESET "Connection reset by server".
/// </summary>
public sealed class RequestLogAuditExtensionsTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    private static DefaultHttpContext ContextFor(Guid? tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantProvider>(new FixedTenantProvider(tenantId));
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private static RequestLog Log(Guid tenantId = default, string provider = "chatgpt-acc1") => new()
    {
        Provider = provider,
        Model = "gpt-5.4-mini",
        TenantId = tenantId,
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Stamps_the_authenticated_tenant_when_the_row_has_none()
    {
        var logger = new CapturingRequestLogger();

        await logger.RecordWithContextAsync(Log(), ContextFor(Tenant));

        logger.Recorded.Should().ContainSingle();
        logger.Recorded[0].TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task Fails_closed_when_no_tenant_is_authenticated()
    {
        var logger = new CapturingRequestLogger();

        var act = async () => await logger.RecordWithContextAsync(Log(), ContextFor(null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tenant is required*");
        logger.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Fails_closed_when_the_row_tenant_conflicts_with_the_authenticated_tenant()
    {
        var logger = new CapturingRequestLogger();

        var act = async () => await logger.RecordWithContextAsync(
            Log(tenantId: OtherTenant), ContextFor(Tenant));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match*");
        logger.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Accepts_a_row_that_already_carries_the_authenticated_tenant()
    {
        var logger = new CapturingRequestLogger();

        await logger.RecordWithContextAsync(Log(tenantId: Tenant), ContextFor(Tenant));

        logger.Recorded.Should().ContainSingle();
        logger.Recorded[0].TenantId.Should().Be(Tenant);
    }

    [Theory]
    [InlineData("gemini-acc1", "broker-managed")]
    [InlineData("gemini-subscription", "broker-managed")]
    [InlineData("cliproxyapi", "broker-managed")]
    [InlineData("chatgpt-acc1", "direct")]
    [InlineData("opencode", "direct")]
    public async Task Classifies_the_route_kind_from_the_provider(string provider, string expected)
    {
        var logger = new CapturingRequestLogger();

        await logger.RecordWithContextAsync(Log(provider: provider), ContextFor(Tenant));

        logger.Recorded[0].RouteKind.Should().Be(expected);
    }

    [Fact]
    public async Task Try_variant_does_not_propagate_a_logger_failure()
    {
        var logger = new CapturingRequestLogger
        {
            Failure = new InvalidOperationException("Request log tenant does not match the authenticated tenant."),
        };

        var act = async () => await logger.TryRecordWithContextAsync(Log(), ContextFor(Tenant));

        await act.Should().NotThrowAsync();
        logger.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Try_variant_drops_the_row_when_attribution_cannot_be_resolved()
    {
        var logger = new CapturingRequestLogger();

        var act = async () => await logger.TryRecordWithContextAsync(Log(), ContextFor(null));

        await act.Should().NotThrowAsync();
        logger.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Try_variant_persists_the_stamped_row()
    {
        var logger = new CapturingRequestLogger();

        await logger.TryRecordWithContextAsync(Log(), ContextFor(Tenant));

        logger.Recorded.Should().ContainSingle();
        logger.Recorded[0].TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task Try_variant_counts_a_dropped_row_so_the_gap_is_observable()
    {
        var logger = new CapturingRequestLogger
        {
            Failure = new InvalidOperationException("Request log tenant does not match the authenticated tenant."),
        };

        await logger.TryRecordWithContextAsync(Log(), ContextFor(Tenant));

        // Read through the same snapshot the exporter uses: a delta assertion stays valid even if
        // another test class drops a row concurrently, while still failing outright (0) when the
        // counter is not incremented at all.
        RequestLogAuditMetrics.SnapshotAndResetDropped().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Try_variant_counts_an_unexpected_cancellation_as_a_drop()
    {
        var logger = new CapturingRequestLogger { Failure = new OperationCanceledException() };

        // No caller token (ct = None) => the cancellation came from inside the write, so the row is
        // lost for a reason that is not an expected client disconnect: it must be counted.
        await logger.TryRecordWithContextAsync(Log(), ContextFor(Tenant));

        RequestLogAuditMetrics.SnapshotAndResetDropped().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Try_variant_stays_silent_when_the_caller_cancelled()
    {
        RequestLogAuditMetrics.SnapshotAndResetDropped();
        var before = RequestLogAuditMetrics.DroppedTotal;
        var logger = new CapturingRequestLogger { Failure = new OperationCanceledException() };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await logger.TryRecordWithContextAsync(Log(), ContextFor(Tenant), cts.Token);

        await act.Should().NotThrowAsync();
        (RequestLogAuditMetrics.DroppedTotal - before).Should().Be(0,
            "a caller cancellation is expected and must not inflate the dropped-row signal");
    }

    private sealed class FixedTenantProvider(Guid? tenantId) : ITenantProvider
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class CapturingRequestLogger : IRequestLogger
    {
        public List<RequestLog> Recorded { get; } = [];

        public Exception? Failure { get; init; }

        public Task RecordAsync(RequestLog log, CancellationToken ct = default)
        {
            if (Failure is not null)
                return Task.FromException(Failure);

            Recorded.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RequestLog>> GetRecentAsync(int count = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RequestLog>>([]);

        public Task<RequestLog?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<RequestLog?>(null);

        public Task<IReadOnlyList<RequestLog>> SearchAsync(
            string? providerFilter = null,
            string? modelFilter = null,
            string? apiKeyFilter = null,
            string? searchText = null,
            DateTimeOffset? from = null,
            DateTimeOffset? until = null,
            int maxResults = 100,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RequestLog>>([]);
    }
}
