using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Gateway.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Arkana.Gateway.Api.Tests.Services;

/// <summary>
/// Tests for the profile page's server-side surface:
///  * API-key binding (BindApiKeyToUserAsync)
///  * provider-aware quota sections (GetUsageWindowsAsync → Accounts)
///  * paginated, ownership-scoped request logs (GetOwnedLogsPageAsync /
///    GetOwnedLogByIdAsync)
/// </summary>
public sealed class DashboardProfileTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    // ── Test scaffolding ────────────────────────────────────────────────

    /// <summary>
    /// In-memory IRequestLogger that also implements IRequestLogSummaryReader,
    /// mirroring production (BatchedRequestLogger/EfCoreRequestLogger) so the
    /// paged summary path is exercised instead of the in-memory fallback.
    /// </summary>
    private sealed class FakeLogStore : IRequestLogger, IRequestLogSummaryReader
    {
        public List<RequestLog> Logs { get; } = [];

        public Task RecordAsync(RequestLog log, CancellationToken ct = default)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RequestLog>> GetRecentAsync(int count = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RequestLog>>(
                Logs.OrderByDescending(l => l.Timestamp).Take(count).ToList());

        public Task<RequestLog?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Logs.FirstOrDefault(l => l.Id == id));

        public Task<IReadOnlyList<RequestLog>> SearchAsync(
            string? providerFilter = null, string? modelFilter = null, string? apiKeyFilter = null,
            string? searchText = null, DateTimeOffset? from = null, DateTimeOffset? until = null,
            int maxResults = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RequestLog>>(Logs.Take(maxResults).ToList());

        public Task<IReadOnlyList<RequestLogSummary>> SearchSummariesAsync(
            DateTimeOffset? from = null, DateTimeOffset? until = null, int maxResults = 100,
            CancellationToken ct = default)
        {
            var rows = Logs.OrderByDescending(l => l.Timestamp).Take(maxResults).Select(ToSummary).ToList();
            return Task.FromResult<IReadOnlyList<RequestLogSummary>>(rows);
        }

        public Task<(IReadOnlyList<RequestLogSummary> Items, int TotalCount)> SearchSummariesPageAsync(
            IReadOnlyCollection<string>? apiKeyNames = null, int skip = 0, int take = 100,
            CancellationToken ct = default)
        {
            var query = apiKeyNames is { Count: > 0 }
                ? Logs.Where(l => l.ApiKeyName is not null && apiKeyNames.Contains(l.ApiKeyName))
                : Logs;
            var ordered = query.OrderByDescending(l => l.Timestamp).ToList();
            var items = ordered.Skip(skip).Take(take).Select(ToSummary).ToList();
            return Task.FromResult<(IReadOnlyList<RequestLogSummary>, int)>((items, ordered.Count));
        }

        private static RequestLogSummary ToSummary(RequestLog l) => new(
            l.Id, l.Provider, l.Model, l.ApiKeyName, l.ResolvedProviderAccountId, l.ResolvedProviderAccountCode,
            l.RouteKind, l.IsError, l.InputTokens, l.OutputTokens, l.Duration.Ticks, l.Timestamp, l.ErrorMessage);
    }

    private static (DashboardService Svc, IApiKeyRepository Keys, IAccountUsageSnapshotRepository Snapshots,
        IProviderAccountRepository Accounts, FakeLogStore Logs) Create(
        List<ApiKey>? keys = null, List<AccountUsageSnapshot>? snapshots = null,
        List<ProviderAccount>? accounts = null, List<RequestLog>? logs = null)
    {
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(keys ?? []);
        keyRepo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var tenantId = callInfo.Arg<Guid>();
            return (keys ?? []).Where(k => k.TenantId == tenantId).ToList();
        });

        var snapRepo = Substitute.For<IAccountUsageSnapshotRepository>();
        snapRepo.GetByAccountCodesAsync(
                Arg.Any<Guid>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var requested = callInfo.Arg<IEnumerable<string>?>();
                var source = snapshots ?? [];
                if (requested is null)
                    return source;
                var codes = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
                return source.Where(s => codes.Contains(s.AccountCode)).ToList();
            });

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns([]);

        var accountRepo = Substitute.For<IProviderAccountRepository>();
        accountRepo.GetByCodeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var code = callInfo.ArgAt<string>(1);
                return (accounts ?? []).FirstOrDefault(a => a.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            });

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(Tenant);

        var logStore = new FakeLogStore();
        if (logs is not null)
            logStore.Logs.AddRange(logs);

        var svc = new DashboardService(
            providerRepo,
            keyRepo,
            Substitute.For<IModelRepository>(),
            Substitute.For<ITokenTracker>(),
            logStore,
            new ActiveStreamCounter(),
            Substitute.For<IN8nService>(),
            new UserTimeService(Substitute.For<IConfiguration>()),
            Substitute.For<ICredentialVault>(),
            new UrlSafetyValidator(new UrlSafetyOptions { AllowHttp = true, AllowPrivateAddresses = true }, new DnsResolver()),
            Substitute.For<System.Net.Http.IHttpClientFactory>(),
            Substitute.For<IApiKeyPoolRepository>(),
            Substitute.For<IAgentRepository>(),
            new AgentOrchestrator(
                Substitute.For<IAgentRepository>(),
                Substitute.For<IModelRepository>(),
                Substitute.For<IAiProviderRepository>(),
                Substitute.For<Arkana.Domain.Interfaces.Canonical.IProviderConnectorFactory>(),
                Substitute.For<ILogger<AgentOrchestrator>>()),
            Substitute.For<IOAuthFlowService>(),
            Substitute.For<IOAuthProviderConfigRepository>(),
            Substitute.For<Microsoft.AspNetCore.Components.NavigationManager>(),
            snapRepo,
            accountRepo,
            tenantProvider);

        return (svc, keyRepo, snapRepo, accountRepo, logStore);
    }

    private static ApiKey OwnedKey(Guid owner, string name, string? pin)
    {
        var key = ApiKey.Create(name, $"hash-{name}", "arkana-test", Tenant);
        key.BindToUser(owner);
        key.PreferredProviderCode = pin;
        return key;
    }

    private static RequestLog LogFor(string apiKeyName, DateTimeOffset when, bool isError = false,
        int inputTokens = 10, int outputTokens = 5, string? response = "ok", Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Provider = "openai",
            Model = "gpt-5.6-luna",
            ApiKeyName = apiKeyName,
            TenantId = Tenant,
            Messages = [new ChatMessage { Role = "user", Content = "hello there" }],
            ResponseContent = response,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = 0.00021m,
            Duration = TimeSpan.FromMilliseconds(812),
            Timestamp = when,
            IsError = isError,
            ErrorMessage = isError ? "upstream 502" : null,
            RouteKind = "broker-managed",
        };

    // ── 1. API-key binding ──────────────────────────────────────────────

    [Fact]
    public async Task BindApiKey_BindsOwnedKeyAndReportsSuccess()
    {
        var owner = Guid.NewGuid();
        var key = ApiKey.Create("profile-key", ApiKeyHasher.Hash("arkana-secret-value"), "arkana-sec", Tenant);
        var (svc, keyRepo, _, _, _) = Create(keys: [key]);

        var result = await svc.BindApiKeyToUserAsync(owner, "arkana-secret-value");

        Assert.True(result.Success);
        Assert.Equal(owner, key.OwnerUserId);
        await keyRepo.Received(1).UpdateAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BindApiKey_TrimsWhitespaceBeforeHashing()
    {
        var owner = Guid.NewGuid();
        var key = ApiKey.Create("profile-key", ApiKeyHasher.Hash("arkana-secret-value"), "arkana-sec", Tenant);
        var (svc, _, _, _, _) = Create(keys: [key]);

        var result = await svc.BindApiKeyToUserAsync(owner, "  arkana-secret-value\n");

        Assert.True(result.Success);
        Assert.Equal(owner, key.OwnerUserId);
    }

    [Fact]
    public async Task BindApiKey_RebindBySameOwnerIsIdempotent()
    {
        var owner = Guid.NewGuid();
        var key = ApiKey.Create("profile-key", ApiKeyHasher.Hash("arkana-secret-value"), "arkana-sec", Tenant);
        key.BindToUser(owner);
        var (svc, keyRepo, _, _, _) = Create(keys: [key]);

        var result = await svc.BindApiKeyToUserAsync(owner, "arkana-secret-value");

        Assert.True(result.Success);
        Assert.Equal(owner, key.OwnerUserId);
        await keyRepo.Received(1).UpdateAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BindApiKey_UnknownSecretIsRejected()
    {
        var (svc, keyRepo, _, _, _) = Create(keys: [ApiKey.Create("other", ApiKeyHasher.Hash("real-secret"), "arkana-rea", Tenant)]);

        var result = await svc.BindApiKeyToUserAsync(Guid.NewGuid(), "not-a-real-secret");

        Assert.False(result.Success);
        Assert.Equal("The API key was not found.", result.Message);
        await keyRepo.DidNotReceive().UpdateAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BindApiKey_KeyOwnedByAnotherProfileIsRefused()
    {
        var otherUser = Guid.NewGuid();
        var key = ApiKey.Create("someone-elses-key", ApiKeyHasher.Hash("shared-secret"), "arkana-key", Tenant);
        key.BindToUser(otherUser);
        var (svc, keyRepo, _, _, _) = Create(keys: [key]);

        var result = await svc.BindApiKeyToUserAsync(Guid.NewGuid(), "shared-secret");

        Assert.False(result.Success);
        Assert.Equal("This API key is already bound to another profile.", result.Message);
        Assert.Equal(otherUser, key.OwnerUserId);
        await keyRepo.DidNotReceive().UpdateAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BindApiKey_KeyFromAnotherTenantIsNotFound()
    {
        var key = ApiKey.Create(
            "other-tenant-key",
            ApiKeyHasher.Hash("cross-tenant-secret"),
            "arkana-key",
            Guid.NewGuid());
        var (svc, keyRepo, _, _, _) = Create(keys: [key]);

        var result = await svc.BindApiKeyToUserAsync(Guid.NewGuid(), "cross-tenant-secret");

        Assert.False(result.Success);
        Assert.Equal("The API key was not found.", result.Message);
        Assert.Null(key.OwnerUserId);
        await keyRepo.DidNotReceive().UpdateAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BindApiKey_EmptySecretIsRejected(string secret)
    {
        var (svc, _, _, _, _) = Create();

        var result = await svc.BindApiKeyToUserAsync(Guid.NewGuid(), secret);

        Assert.False(result.Success);
        Assert.Equal("A valid API key is required.", result.Message);
    }

    [Fact]
    public async Task BindApiKey_EmptyOwnerIsRejected()
    {
        var (svc, _, _, _, _) = Create();

        var result = await svc.BindApiKeyToUserAsync(Guid.Empty, "arkana-secret-value");

        Assert.False(result.Success);
        Assert.Equal("A valid API key is required.", result.Message);
    }

    // ── 2. Provider-aware quota sections ────────────────────────────────

    [Theory]
    [InlineData("chatgpt-acc3", "codex")]
    [InlineData("CHATGPT-ACC7", "codex")]
    [InlineData("gemini-acc3", "antigravity")]
    [InlineData("Gemini-Acc1", "antigravity")]
    [InlineData("opencode", "other")]
    [InlineData("ollama", "other")]
    public void ClassifyQuotaKind_UsesTheRoutingConvention(string code, string expected)
        => Assert.Equal(expected, DashboardService.ClassifyQuotaKind(code));

    [Fact]
    public async Task UsageWindows_AntigravityPin_ShowsAntigravityQuotaOnly()
    {
        var owner = Guid.NewGuid();
        var account = ProviderAccount.Create(Tenant, Guid.NewGuid(), "gemini-acc3", "Gemini Pro (acc3)");
        account.MarkConnected();
        account.RecordSuccess(DateTimeOffset.UtcNow.AddMinutes(-4));
        account.SetCooldown(DateTimeOffset.UtcNow.AddHours(2), "Quota");

        var (svc, _, _, _, _) = Create(
            keys: [OwnedKey(owner, "gemini-key", "gemini-acc3")],
            accounts: [account]);

        var view = await svc.GetUsageWindowsAsync(owner);

        Assert.False(view.HasCodex);
        Assert.True(view.HasAntigravity);
        var pinned = Assert.Single(view.Accounts);
        Assert.Equal("gemini-acc3", pinned.AccountCode);
        Assert.Equal("antigravity", pinned.Kind);
        Assert.Equal("Gemini Pro (acc3)", pinned.DisplayName);
        Assert.Equal(ProviderAccountStatus.Connected.ToString(), pinned.Status);
        Assert.NotNull(pinned.QuotaCooldownUntil);
        Assert.Equal("Quota", pinned.LastFailureClass);
        Assert.NotNull(pinned.LastSuccessAt);
        Assert.False(pinned.HasSnapshots);
        Assert.Empty(pinned.Windows);
    }

    [Fact]
    public async Task UsageWindows_CodexPin_ShowsCodexWindowsOnly()
    {
        var owner = Guid.NewGuid();
        var snapshot = new AccountUsageSnapshot(
            Guid.NewGuid(), "chatgpt-acc3", UsageWindowKind.Primary, 42.5, 300,
            DateTimeOffset.UtcNow.AddHours(3), "plus");

        var (svc, _, _, _, _) = Create(
            keys: [OwnedKey(owner, "codex-key", "chatgpt-acc3")],
            snapshots: [snapshot]);

        var view = await svc.GetUsageWindowsAsync(owner);

        Assert.True(view.HasCodex);
        Assert.False(view.HasAntigravity);
        var pinned = Assert.Single(view.Accounts);
        Assert.Equal("codex", pinned.Kind);
        Assert.True(pinned.HasSnapshots);
        Assert.Equal("tracked", pinned.Status);
        var window = Assert.Single(pinned.Windows);
        Assert.Equal("5h", window.Window);
        Assert.Equal(42.5, window.UsedPercent);
    }

    [Fact]
    public async Task UsageWindows_BothKindsPinned_ReportsBothSections()
    {
        var owner = Guid.NewGuid();
        var account = ProviderAccount.Create(Tenant, Guid.NewGuid(), "gemini-acc1", "Gemini Pro (acc1)");
        account.MarkConnected();
        var snapshot = new AccountUsageSnapshot(
            Guid.NewGuid(), "chatgpt-acc7", UsageWindowKind.Secondary, 12.0, 10080,
            DateTimeOffset.UtcNow.AddDays(2), "pro");

        var (svc, _, _, _, _) = Create(
            keys: [OwnedKey(owner, "mixed-codex", "chatgpt-acc7"), OwnedKey(owner, "mixed-gemini", "gemini-acc1")],
            snapshots: [snapshot],
            accounts: [account]);

        var view = await svc.GetUsageWindowsAsync(owner);

        Assert.True(view.HasCodex);
        Assert.True(view.HasAntigravity);
        Assert.Equal(2, view.Accounts.Count);
        Assert.Contains(view.Accounts, a => a is { Kind: "codex", AccountCode: "chatgpt-acc7" });
        Assert.Contains(view.Accounts, a => a is { Kind: "antigravity", AccountCode: "gemini-acc1" });
    }

    [Fact]
    public async Task UsageWindows_NoKeys_UsesNoQuotaSection()
    {
        var (svc, _, _, _, _) = Create();

        var view = await svc.GetUsageWindowsAsync(Guid.NewGuid());

        Assert.False(view.HasCodex);
        Assert.False(view.HasAntigravity);
        Assert.Empty(view.Accounts);
    }

    // ── 3. Paginated, ownership-scoped logs ─────────────────────────────

    [Fact]
    public async Task OwnedLogsPage_PagesOnlyThisProfilesKeys()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var when = DateTimeOffset.UtcNow;
        var logs = new List<RequestLog>();
        for (var i = 0; i < 7; i++)
            logs.Add(LogFor("mine", when.AddMinutes(-i), id: Guid.Parse($"00000000-0000-0000-0000-0000000000{i + 10:x2}")));
        logs.Add(LogFor("theirs", when.AddMinutes(-30)));

        var (svc, _, _, _, _) = Create(
            keys: [OwnedKey(owner, "mine", "gemini-acc3"), OwnedKey(other, "theirs", "chatgpt-acc3")],
            logs: logs);

        var (page1, total1) = await svc.GetOwnedLogsPageAsync(owner, page: 1, pageSize: 5);
        var (page2, total2) = await svc.GetOwnedLogsPageAsync(owner, page: 2, pageSize: 5);

        Assert.Equal(7, total1);
        Assert.Equal(7, total2);
        Assert.Equal(5, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.All(page1.Concat(page2), row => Assert.Equal("mine", row.ApiKeyName));
        // Newest first and no overlap between pages.
        Assert.True(page1[0].Timestamp > page1[^1].Timestamp);
        Assert.Empty(page1.Select(p => p.Id).Intersect(page2.Select(p => p.Id)));
    }

    [Fact]
    public async Task OwnedLogsPage_PageSizeIsClampedAndEmptyWithoutKeys()
    {
        var owner = Guid.NewGuid();
        var (svc, _, _, _, _) = Create(keys: [OwnedKey(owner, "mine", "gemini-acc3")],
            logs: [LogFor("mine", DateTimeOffset.UtcNow)]);

        var (rows, total) = await svc.GetOwnedLogsPageAsync(owner, page: 1, pageSize: 9_999);
        Assert.Single(rows);
        Assert.Equal(1, total);

        var (none, zero) = await svc.GetOwnedLogsPageAsync(Guid.NewGuid(), page: 1, pageSize: 25);
        Assert.Empty(none);
        Assert.Equal(0, zero);
    }

    [Fact]
    public async Task OwnedLogsPage_SummaryRowsCarryNoPayload()
    {
        var owner = Guid.NewGuid();
        var log = LogFor("mine", DateTimeOffset.UtcNow, inputTokens: 120, outputTokens: 30);

        var (svc, _, _, _, _) = Create(keys: [OwnedKey(owner, "mine", "gemini-acc3")], logs: [log]);

        var (rows, _) = await svc.GetOwnedLogsPageAsync(owner, 1, 25);

        var row = Assert.Single(rows);
        Assert.Equal(log.Id, row.Id);
        Assert.Equal(150, row.TotalTokens);
        Assert.Equal(812, row.DurationMs);
        Assert.Equal("broker-managed", row.RouteKind);
        Assert.Equal("mine", row.ApiKeyName);
    }

    [Fact]
    public async Task OwnedLogById_ReturnsFullLogForOwnedKey()
    {
        var owner = Guid.NewGuid();
        var log = LogFor("mine", DateTimeOffset.UtcNow);

        var (svc, _, _, _, _) = Create(keys: [OwnedKey(owner, "mine", "gemini-acc3")], logs: [log]);

        var full = await svc.GetOwnedLogByIdAsync(owner, log.Id);

        Assert.NotNull(full);
        Assert.Equal(log.Id, full.Id);
        var message = Assert.Single(full.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("ok", full.ResponseContent);
    }

    [Fact]
    public async Task OwnedLogById_RefusesAnotherProfilesLog()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var foreign = LogFor("theirs", DateTimeOffset.UtcNow);

        var (svc, _, _, _, _) = Create(
            keys: [OwnedKey(owner, "mine", "gemini-acc3"), OwnedKey(other, "theirs", "chatgpt-acc3")],
            logs: [foreign]);

        Assert.Null(await svc.GetOwnedLogByIdAsync(owner, foreign.Id));
    }

    [Fact]
    public async Task OwnedLogById_UnknownIdAndOwnerWithoutKeysReturnNull()
    {
        var owner = Guid.NewGuid();
        var log = LogFor("mine", DateTimeOffset.UtcNow);
        var (svc, _, _, _, _) = Create(keys: [OwnedKey(owner, "mine", "gemini-acc3")], logs: [log]);

        Assert.Null(await svc.GetOwnedLogByIdAsync(owner, Guid.NewGuid()));
        Assert.Null(await svc.GetOwnedLogByIdAsync(Guid.NewGuid(), log.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OwnedLogById_LogWithoutKeyNameIsNeverExposed(string? apiKeyName)
    {
        var owner = Guid.NewGuid();
        var log = LogFor("mine", DateTimeOffset.UtcNow) with { ApiKeyName = apiKeyName };
        var (svc, _, _, _, _) = Create(keys: [OwnedKey(owner, "mine", "gemini-acc3")], logs: [log]);

        Assert.Null(await svc.GetOwnedLogByIdAsync(owner, log.Id));
    }
}
