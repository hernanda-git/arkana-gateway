using System.Net;
using System.Net.Http.Headers;
using Arkana.Domain.Entities;
using Arkana.Infrastructure.AI;
using FluentAssertions;
using Xunit;

namespace Arkana.Infrastructure.Tests.AI;

/// <summary>
/// Tests for the ChatGPT Codex usage-window capture (profile page):
/// parsing the upstream x-codex-* header family and the codex.rate_limits SSE
/// event, and converting a parsed report into persisted AccountUsageSnapshot
/// rows via the connector's capture hook.
/// </summary>
public sealed class ChatGptCodexUsageCaptureTests
{
    private static HttpResponseMessage Response(Action<HttpResponseHeaders>? headers = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        headers?.Invoke(resp.Headers);
        return resp;
    }

    // ── Header family (x-codex-primary-*/x-codex-secondary-*) ──

    [Fact]
    public void ParseRateLimitHeaders_ReadsPrimaryAndSecondary()
    {
        var resp = Response(h =>
        {
            h.TryAddWithoutValidation("x-codex-primary-used-percent", "42.5");
            h.TryAddWithoutValidation("x-codex-primary-window-minutes", "299");
            h.TryAddWithoutValidation("x-codex-primary-reset-at", "1790000000");
            h.TryAddWithoutValidation("x-codex-secondary-used-percent", "15");
            h.TryAddWithoutValidation("x-codex-secondary-window-minutes", "10079");
            h.TryAddWithoutValidation("x-codex-secondary-reset-at", "1795000000");
            h.TryAddWithoutValidation("x-codex-plan-type", "plus");
        });

        var report = ChatGptCodexChatService.ParseRateLimitHeaders(resp.Headers);

        report.Should().NotBeNull();
        report!.HasAny.Should().BeTrue();
        report.Primary!.UsedPercent.Should().Be(42.5);
        report.Primary.WindowMinutes.Should().Be(299);
        report.Primary.ResetAtEpochSeconds.Should().Be(1790000000);
        report.Secondary!.UsedPercent.Should().Be(15);
        report.Secondary.WindowMinutes.Should().Be(10079);
        report.PlanType.Should().Be("plus");
    }

    [Fact]
    public void ParseRateLimitHeaders_ReturnsNull_WhenNoRateLimitHeaders()
    {
        var resp = Response();
        ChatGptCodexChatService.ParseRateLimitHeaders(resp.Headers).Should().BeNull();
    }

    [Fact]
    public void ParseRateLimitHeaders_ToleratesGarbageValues()
    {
        var resp = Response(h =>
        {
            h.TryAddWithoutValidation("x-codex-primary-used-percent", "not-a-number");
            h.TryAddWithoutValidation("x-codex-secondary-used-percent", "7.25");
        });

        var report = ChatGptCodexChatService.ParseRateLimitHeaders(resp.Headers);

        report.Should().NotBeNull();
        report!.Primary.Should().BeNull();      // garbage percent -> no window
        report.Secondary!.UsedPercent.Should().Be(7.25);
    }

    [Fact]
    public void ParseRateLimitHeaders_KeepsZeroPercentWindow()
    {
        // 0% is meaningful (fresh window) — must NOT be dropped.
        var resp = Response(h => h.TryAddWithoutValidation("x-codex-primary-used-percent", "0"));

        var report = ChatGptCodexChatService.ParseRateLimitHeaders(resp.Headers);
        report!.Primary!.UsedPercent.Should().Be(0);
    }

    // ── codex.rate_limits SSE event payload ──

    [Fact]
    public void ParseRateLimitEventPayload_ReadsBothWindows()
    {
        const string json = """
            {"type":"codex.rate_limits","plan_type":"team",
             "rate_limits":{"primary":{"used_percent":87,"window_minutes":300,"reset_at":1790000000},
                            "secondary":{"used_percent":12.5,"window_minutes":10080,"reset_at":1795000000}}}
            """;

        var report = ChatGptCodexChatService.TryParseRateLimitEventForTests(json);

        report.Should().NotBeNull();
        report!.Primary!.UsedPercent.Should().Be(87);
        report.Primary.WindowMinutes.Should().Be(300);
        report.Secondary!.UsedPercent.Should().Be(12.5);
        report.PlanType.Should().Be("team");
    }

    [Fact]
    public void ParseRateLimitEventPayload_IgnoresOtherEvents()
    {
        const string json = """{"type":"response.completed","response":{}}""";
        ChatGptCodexChatService.TryParseRateLimitEventForTests(json).Should().BeNull();
    }

    [Fact]
    public void ParseRateLimitEventPayload_ToleratesMalformedJson()
    {
        ChatGptCodexChatService.TryParseRateLimitEventForTests("{not json").Should().BeNull();
    }

    // ── Report → AccountUsageSnapshot mapping (the capture hook's core) ──

    [Fact]
    public async Task CaptureUsageSnapshot_PersistsLatestPerAccountAndWindow()
    {
        var repo = new FakeRepo();
        var account = new ChatGptAccount(Guid.NewGuid(), "chatgpt-acc2", "tok", "acct-id");

        var service = TestServiceFactory(repo);
        service.CaptureForTest(account, new ChatGptCodexChatService.CodexRateLimitReport(
            new ChatGptCodexChatService.CodexUsageWindow(40, 300, 1790000000),
            new ChatGptCodexChatService.CodexUsageWindow(10, 10080, 1795000000),
            "plus"));
        await WaitForAsync(repo, 2);

        repo.Snapshots.Should().HaveCount(2);
        var primary = repo.Snapshots.Single(s => s.WindowKind == UsageWindowKind.Primary);
        primary.AccountCode.Should().Be("chatgpt-acc2");
        primary.AccountProviderId.Should().Be(account.ProviderId);
        primary.UsedPercent.Should().Be(40);
        primary.ResetsAtUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790000000));
        primary.PlanType.Should().Be("plus");

        // Second observation overwrites (last-writer-wins) instead of adding rows.
        service.CaptureForTest(account, new ChatGptCodexChatService.CodexRateLimitReport(
            new ChatGptCodexChatService.CodexUsageWindow(55, 300, 1790000100), null, null));
        await WaitForAsync(repo, 3);

        repo.Snapshots.Should().HaveCount(2);
        repo.Upserts.Should().Be(3);
        repo.Snapshots.Single(s => s.WindowKind == UsageWindowKind.Primary).UsedPercent.Should().Be(55);
    }

    private static async Task WaitForAsync(FakeRepo repo, int expectedUpserts)
    {
        // The capture hook persists on a fire-and-forget task; poll until the
        // expected upsert count is visible (bounded, deterministic).
        for (var i = 0; i < 100 && repo.Upserts < expectedUpserts; i++)
            await Task.Delay(20);
    }

    private static ChatGptCodexChatService TestServiceFactory(FakeRepo repo)
    {
        return ChatGptCodexChatServiceTestsHarness.Create(repo);
    }

    private sealed class FakeRepo : Arkana.Domain.Interfaces.IAccountUsageSnapshotRepository
    {
        public List<AccountUsageSnapshot> Snapshots { get; } = [];
        public int Upserts { get; private set; }

        public Task<List<AccountUsageSnapshot>> GetByAccountCodesAsync(
            Guid tenantId, IEnumerable<string>? accountCodes = null, CancellationToken ct = default)
            => Task.FromResult(Snapshots
                .Where(s => s.TenantId == Guid.Empty || s.TenantId == tenantId)
                .Where(s => accountCodes is null || accountCodes.Contains(s.AccountCode))
                .ToList());

        public Task<bool> UpsertAsync(AccountUsageSnapshot snapshot, CancellationToken ct = default)
        {
            Upserts++;
            var existing = Snapshots.FirstOrDefault(s =>
                s.AccountProviderId == snapshot.AccountProviderId && s.WindowKind == snapshot.WindowKind);
            if (existing is null)
                Snapshots.Add(snapshot);
            else
                existing.UpdateFrom(snapshot.UsedPercent, snapshot.WindowMinutes,
                    snapshot.ResetsAtUtc, snapshot.PlanType);
            return Task.FromResult(true);
        }
    }
}
