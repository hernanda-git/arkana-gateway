using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// In-process service wrapping repositories for Blazor dashboard pages.
/// </summary>
public sealed class DashboardService
{
    private readonly IAiProviderRepository _providers;
    private readonly IApiKeyRepository _apiKeys;
    private readonly IModelRepository _models;
    private readonly ITokenTracker _tracker;
    private readonly IRequestLogger _fullLogger;
    private readonly IRequestLogSummaryReader? _summaryReader;
    private readonly ActiveStreamCounter _streamCounter;
    private readonly IN8nService _n8n;
    private readonly UserTimeService _userTime;
    private readonly ICredentialVault _vault;
    private readonly UrlSafetyValidator _urlValidator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApiKeyPoolRepository _keyPoolRepo;
    private readonly IAgentRepository _agents;
    private readonly AgentOrchestrator _agentOrchestrator;
    private readonly IOAuthFlowService _oauth;
    private readonly IOAuthProviderConfigRepository _oauthConfigs;
    private readonly Microsoft.AspNetCore.Components.NavigationManager _nav;
    private readonly IAccountUsageSnapshotRepository? _usageSnapshots;
    private readonly IProviderAccountRepository? _providerAccounts;
    private readonly ITenantProvider? _tenantProvider;
    private readonly ProviderAccountDashboardFacade? _providerAccountDashboard;

    public DashboardService(IAiProviderRepository providers, IApiKeyRepository apiKeys,
        IModelRepository models, ITokenTracker tracker,
        IRequestLogger fullLogger, ActiveStreamCounter streamCounter,
        IN8nService n8n, UserTimeService userTime,
        ICredentialVault vault, UrlSafetyValidator urlValidator,
        IHttpClientFactory httpClientFactory,
        IApiKeyPoolRepository keyPoolRepo,
        IAgentRepository agents,
        AgentOrchestrator agentOrchestrator,
        IOAuthFlowService oauth,
        IOAuthProviderConfigRepository oauthConfigs,
        Microsoft.AspNetCore.Components.NavigationManager nav,
        IAccountUsageSnapshotRepository? usageSnapshots = null,
        IProviderAccountRepository? providerAccounts = null,
        ITenantProvider? tenantProvider = null,
        ProviderAccountDashboardFacade? providerAccountDashboard = null)
    {
        _providers = providers;
        _apiKeys = apiKeys;
        _models = models;
        _tracker = tracker;
        _fullLogger = fullLogger;
        _summaryReader = fullLogger as IRequestLogSummaryReader;
        _streamCounter = streamCounter;
        _n8n = n8n;
        _userTime = userTime;
        _vault = vault;
        _urlValidator = urlValidator;
        _httpClientFactory = httpClientFactory;
        _keyPoolRepo = keyPoolRepo;
        _agents = agents;
        _agentOrchestrator = agentOrchestrator;
        _oauth = oauth;
        _oauthConfigs = oauthConfigs;
        _nav = nav;
        _usageSnapshots = usageSnapshots;
        _providerAccounts = providerAccounts;
        _tenantProvider = tenantProvider;
        _providerAccountDashboard = providerAccountDashboard;
    }

    /// <summary>Backward-compatible overload (without vault/urlValidator/httpClientFactory).</summary>
    public DashboardService(IAiProviderRepository providers, IApiKeyRepository apiKeys,
        IModelRepository models, ITokenTracker tracker,
        IRequestLogger fullLogger, ActiveStreamCounter streamCounter,
        IN8nService n8n, UserTimeService userTime)
        : this(providers, apiKeys, models, tracker, fullLogger, streamCounter, n8n, userTime, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
    {
    }

    private TimeSpan TzOffset => _userTime.ToLocal(DateTimeOffset.UtcNow).Offset;

    private Guid RequireTenant(string operation)
        => _tenantProvider?.TenantId
            ?? throw new InvalidOperationException($"Authenticated tenant is required to {operation}.");

    // ── Dashboard Stats (static) ─────────────────────────────

    public sealed record DashboardStats(
        int TotalApiKeys,
        int ActiveApiKeys,
        int TotalProviders,
        int ActiveProviders,
        int ActiveStreams,
        int TotalWorkflows,
        int ActiveWorkflows);

    public async Task<DashboardStats> GetDashboardStatsAsync(bool includeWorkflows = true)
    {
        var providers = await _providers.GetAllAsync();
        var keys = await _apiKeys.GetAllAsync();
        var workflows = includeWorkflows
            ? await _n8n.GetWorkflowsAsync()
            : [];
        return new DashboardStats(keys.Count, keys.Count(k => k.IsActive),
            providers.Count, providers.Count(p => p.IsEnabled),
            _streamCounter.ActiveCount,
            workflows.Count, workflows.Count(w => w.Active));
    }

    /// <summary>Loads the optional n8n workflow counters independently.</summary>
    public async Task<(int Total, int Active)> GetWorkflowStatsAsync()
    {
        var workflows = await _n8n.GetWorkflowsAsync();
        return (workflows.Count, workflows.Count(w => w.Active));
    }

    // ── Usage Stats (time-range aware) ───────────────────────

    public enum TimeRange { Daily, Weekly, Monthly, Yearly, All }

    public sealed record UsageStats(
        int Requests,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        decimal Cost);

    public async Task<UsageStats> GetUsageStatsAsync(TimeRange range,
        string? providerFilter = null, string? apiKeyFilter = null,
        IReadOnlySet<string>? allowedApiKeyNames = null,
        IReadOnlySet<string>? allowedProviderNames = null)
    {
        var (from, _) = GetDateRange(range);
        var usages = await _tracker.GetUsageAsync(from, DateTimeOffset.UtcNow);
        usages = ApplyFilters(usages, providerFilter, apiKeyFilter);
        if (allowedApiKeyNames is not null)
            usages = usages.Where(u => u.ApiKeyName is not null && allowedApiKeyNames.Contains(u.ApiKeyName)).ToList();
        if (allowedProviderNames is not null)
            usages = usages.Where(u => allowedProviderNames.Contains(u.Provider)).ToList();
        return new UsageStats(
            usages.Count,
            usages.Sum(u => u.InputTokens),
            usages.Sum(u => u.OutputTokens),
            usages.Sum(u => u.TotalTokens),
            usages.Sum(u => u.Cost));
    }

    // ── Filter helpers ───────────────────────────────────────

    private static IReadOnlyList<TokenUsage> ApplyFilters(IReadOnlyList<TokenUsage> usages,
        string? providerFilter, string? apiKeyFilter)
    {
        if (!string.IsNullOrEmpty(providerFilter) && providerFilter != "*")
            usages = usages.Where(u =>
                u.Provider.Equals(providerFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(apiKeyFilter) && apiKeyFilter != "*")
            usages = usages.Where(u =>
                string.Equals(u.ApiKeyName, apiKeyFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        return usages;
    }

    public async Task<List<string>> GetProviderNamesAsync()
    {
        var providers = await _providers.GetAllAsync();
        return providers.Select(p => p.Name).Order().ToList();
    }

    public async Task<List<string>> GetApiKeyNamesAsync()
    {
        var keys = await _apiKeys.GetAllAsync();
        return keys.Select(k => k.Name).Order().ToList();
    }

    // ── Model-level Usage (grouped by provider in UI) ────────

    public sealed record ModelUsageView(
        Guid ProviderId, string ProviderName, bool ProviderEnabled,
        Guid ModelId, string ModelName, string ModelCode, bool ModelEnabled,
        int Requests, int InputTokens, int OutputTokens, int TotalTokens, decimal Cost);

    public async Task<List<ModelUsageView>> GetModelUsageAsync(TimeRange range,
        string? providerFilter = null, string? apiKeyFilter = null,
        IReadOnlySet<string>? allowedApiKeyNames = null,
        IReadOnlySet<string>? allowedProviderNames = null)
    {
        var (from, _) = GetDateRange(range);
        IReadOnlyList<AiProvider> allProviders;
        IReadOnlyList<Model> allModels;
        if (_tenantProvider?.TenantId is { } tenantId)
        {
            allProviders = await _providers.GetAllAsync(tenantId);
            allModels = await _models.GetAllAsync(tenantId);
        }
        else if (allowedApiKeyNames is not null)
        {
            // Profile-owned views fail closed when the circuit has no tenant.
            return [];
        }
        else
        {
            allProviders = await _providers.GetAllAsync();
            allModels = await _models.GetAllAsync();
        }
        var usages = await _tracker.GetUsageAsync(from, DateTimeOffset.UtcNow);
        usages = ApplyFilters(usages, providerFilter, apiKeyFilter);
        if (allowedApiKeyNames is not null)
            usages = usages.Where(u => u.ApiKeyName is not null && allowedApiKeyNames.Contains(u.ApiKeyName)).ToList();
        if (allowedProviderNames is not null)
            usages = usages.Where(u => allowedProviderNames.Contains(u.Provider)).ToList();

        var results = new List<ModelUsageView>();

        foreach (var provider in allProviders)
        {
            var providerModels = allModels.Where(m => m.ProviderId == provider.Id).ToList();

            // If no models configured, show one provider row without model detail
            if (providerModels.Count == 0)
            {
                var providerUsages = usages.Where(u =>
                    u.Provider.Equals(provider.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                results.Add(new ModelUsageView(
                    provider.Id, provider.Name, provider.IsEnabled,
                    Guid.Empty, "", "", false,
                    providerUsages.Count,
                    providerUsages.Sum(u => u.InputTokens),
                    providerUsages.Sum(u => u.OutputTokens),
                    providerUsages.Sum(u => u.TotalTokens),
                    providerUsages.Sum(u => u.Cost)));
                continue;
            }

            foreach (var model in providerModels)
            {
                // Model codes are not globally unique: the same code can be
                // configured under several providers. Include the provider in
                // the aggregation key or one usage row is rendered once for
                // every provider that happens to expose that model code.
                var modelUsages = usages.Where(u =>
                    u.Model.Equals(model.Code, StringComparison.OrdinalIgnoreCase)
                    && (u.Provider.Equals(provider.Name, StringComparison.OrdinalIgnoreCase)
                        || u.Provider.Equals(provider.Code, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                results.Add(new ModelUsageView(
                    provider.Id, provider.Name, provider.IsEnabled,
                    model.Id, model.Name, model.Code, model.IsEnabled,
                    modelUsages.Count,
                    modelUsages.Sum(u => u.InputTokens),
                    modelUsages.Sum(u => u.OutputTokens),
                    modelUsages.Sum(u => u.TotalTokens),
                    modelUsages.Sum(u => u.Cost)));
            }
        }

        return results;
    }

    // ── Helpers ──────────────────────────────────────────────

    private (DateTimeOffset From, DateTimeOffset To) GetDateRange(TimeRange range)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        // _navigationDate is already in the configured timezone (set by Dashboard.razor via UserTime)
        var now = _navigationDate ?? _userTime.ToLocal(nowUtc);
        var offset = now.Offset;

        var from = range switch
        {
            TimeRange.Daily => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, offset),
            TimeRange.Weekly => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, offset).AddDays(-6),
            TimeRange.Monthly => new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, offset),
            TimeRange.Yearly => new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, offset),
            TimeRange.All => DateTimeOffset.MinValue,
            _ => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, offset)
        };
        var to = range switch
        {
            TimeRange.Daily => from.AddDays(1),
            TimeRange.Weekly => from.AddDays(7),
            TimeRange.Monthly => from.AddMonths(1),
            TimeRange.Yearly => from.AddYears(1),
            TimeRange.All => DateTimeOffset.MaxValue,
            _ => from.AddDays(1)
        };
        return (from, to);
    }

    // ── Navigation support ──────────────────────────────────

    private DateTimeOffset? _navigationDate;

    public DateTimeOffset? NavigationDate
    {
        get => _navigationDate;
        set => _navigationDate = value;
    }

    public string GetNavigationLabel(TimeRange range)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        // _navigationDate is already in the configured timezone (set by Dashboard.razor via UserTime)
        var now = _navigationDate ?? _userTime.ToLocal(nowUtc);
        return range switch
        {
            TimeRange.Daily => now.ToString("ddd, MMM d"),
            TimeRange.Weekly =>
                $"{now.AddDays(-6):MMM d} – {now:MMM d, yyyy}",
            TimeRange.Monthly => now.ToString("MMMM yyyy"),
            TimeRange.Yearly => now.ToString("yyyy"),
            TimeRange.All => "All Time",
            _ => now.ToString("MMM d")
        };
    }

    // ── Provider toggle ──────────────────────────────────────

    public sealed record ProviderView(
        Guid Id, string Name, string Code, string? BaseUrl,
        bool IsEnabled, int Priority, string AuthMethod, string? OAuthConfigCode,
        decimal CostPerInputToken, decimal CostPerOutputToken,
        int? MaxTokensPerRequest, DateTimeOffset CreatedAt, Guid? OAuthConfigId);

    public async Task<List<ProviderView>> GetProvidersAsync()
    {
        var list = await GetVisibleProvidersAsync();
        return list.Select(p => new ProviderView(
            p.Id, p.Name, p.Code, p.BaseUrl,
            p.IsEnabled, p.Priority, p.AuthMethod.ToString(), p.OAuthConfig?.ProviderCode,
            p.CostPerInputToken, p.CostPerOutputToken,
            p.MaxTokensPerRequest, p.CreatedAt, p.OAuthConfigId
        )).ToList();
    }

    private async Task<List<AiProvider>> GetVisibleProvidersAsync()
    {
        if (_tenantProvider?.TenantId is not { } tenantId)
            return [];

        var list = (await _providers.GetAllAsync(tenantId)).ToList();
        if (_providerAccountDashboard is null)
            return list;

        var deletedProviderIds = await _providerAccountDashboard.GetDeletedProviderIdsAsync();
        return list.Where(provider => !deletedProviderIds.Contains(provider.Id)).ToList();
    }

    public async Task ToggleProviderAsync(Guid id)
    {
        var tenantId = _tenantProvider?.TenantId
            ?? throw new InvalidOperationException("Authenticated tenant is required to update a provider.");
        var provider = await _providers.GetByIdAsync(id, tenantId);
        if (provider is null) return;
        if (provider.IsEnabled) provider.Disable(); else provider.Enable();
        await _providers.UpdateAsync(provider, tenantId);
    }

    // ── Models ───────────────────────────────────────────────

    public sealed record ModelView(
        Guid Id, Guid ProviderId, string ProviderName, string Name, string Code,
        bool IsEnabled, decimal CostPerInputToken, decimal CostPerOutputToken);

    public async Task<List<ModelView>> GetAllModelsAsync()
    {
        if (_tenantProvider?.TenantId is not { } tenantId)
            return [];

        var list = await _models.GetAllAsync(tenantId);
        return list.Select(m => new ModelView(m.Id, m.ProviderId, m.Provider.Name, m.Name, m.Code,
            m.IsEnabled, m.CostPerInputToken, m.CostPerOutputToken)).ToList();
    }

    public async Task<List<ModelView>> GetModelsByProviderAsync(Guid providerId)
    {
        if (_tenantProvider?.TenantId is not { } tenantId)
            return [];

        var list = await _models.GetByProviderIdAsync(providerId, tenantId);
        return list.Select(m => new ModelView(m.Id, m.ProviderId, m.Provider.Name, m.Name, m.Code,
            m.IsEnabled, m.CostPerInputToken, m.CostPerOutputToken)).ToList();
    }

    public sealed record ProviderWithModels(
        Guid Id, string Name, string Code, List<ModelView> Models);

    public async Task<List<ProviderWithModels>> GetProvidersWithModelsAsync()
    {
        if (_tenantProvider?.TenantId is not { } tenantId)
            return [];

        var providers = await GetVisibleProvidersAsync();
        var allModels = await _models.GetAllAsync(tenantId);

        return providers.Select(p => new ProviderWithModels(
            p.Id, p.Name, p.Code,
            allModels.Where(m => m.ProviderId == p.Id)
                .Select(m => new ModelView(m.Id, m.ProviderId, p.Name, m.Name, m.Code,
                    m.IsEnabled, m.CostPerInputToken, m.CostPerOutputToken))
                .ToList()
        )).ToList();
    }

    // ── API Keys ─────────────────────────────────────────────

    public sealed record ApiKeyView(
        Guid Id, string Name, string Prefix,
        bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
        List<Guid> AllowedModelIds,
        int? RateLimitRpm, int? RateLimitTpm, int? RateLimitMaxConcurrent,
        string? PreferredProviderCode = null,
        bool AllowProviderFallback = false);

    public sealed record ApiKeyRoutingHistoryView(
        string ProviderCode, int Requests, int Errors,
        DateTimeOffset FirstUsedAt, DateTimeOffset LastUsedAt);

    public async Task<List<ApiKeyView>> GetApiKeysAsync()
    {
        var list = await _apiKeys.GetAllAsync();
        return list.Select(k =>
        {
            var prefix = string.IsNullOrEmpty(k.KeyPrefix)
                ? (k.KeyHash.Length > 12 ? k.KeyHash[..12] + "..." : k.KeyHash[..Math.Min(8, k.KeyHash.Length)] + "...")
                : k.KeyPrefix + "...";
            return new ApiKeyView(k.Id, k.Name, prefix, k.IsActive, k.CreatedAt, k.ExpiresAt,
                k.AllowedModels.Select(m => m.Id).ToList(),
                k.RateLimitRpm, k.RateLimitTpm, k.RateLimitMaxConcurrent,
                k.PreferredProviderCode, k.AllowProviderFallback);
        }).ToList();
    }

    private async Task<IReadOnlyList<ApiKey>> GetTenantScopedApiKeysAsync()
    {
        if (_tenantProvider?.TenantId is not { } tenantId)
            return [];
        return await _apiKeys.GetAllAsync(tenantId);
    }

    public async Task<List<ApiKeyView>> GetOwnedApiKeysAsync(Guid ownerUserId)
    {
        var list = await GetTenantScopedApiKeysAsync();
        return list.Where(k => k.OwnerUserId == ownerUserId).Select(k =>
        {
            var prefix = string.IsNullOrEmpty(k.KeyPrefix)
                ? (k.KeyHash.Length > 12 ? k.KeyHash[..12] + "..." : k.KeyHash[..Math.Min(8, k.KeyHash.Length)] + "...")
                : k.KeyPrefix + "...";
            return new ApiKeyView(k.Id, k.Name, prefix, k.IsActive, k.CreatedAt, k.ExpiresAt,
                k.AllowedModels.Select(m => m.Id).ToList(), k.RateLimitRpm, k.RateLimitTpm,
                k.RateLimitMaxConcurrent, k.PreferredProviderCode, k.AllowProviderFallback);
        }).ToList();
    }

    public async Task<(bool Success, string Message)> BindApiKeyToUserAsync(Guid ownerUserId, string plainTextKey)
    {
        if (ownerUserId == Guid.Empty || string.IsNullOrWhiteSpace(plainTextKey))
            return (false, "A valid API key is required.");

        var hash = ApiKeyHasher.Hash(plainTextKey.Trim());
        var keys = await GetTenantScopedApiKeysAsync();
        if (_tenantProvider?.TenantId is null)
            return (false, "Your profile is not associated with a tenant.");

        var key = keys.FirstOrDefault(k => k.KeyHash == hash);
        if (key is null)
            return (false, "The API key was not found.");
        if (key.OwnerUserId.HasValue && key.OwnerUserId != ownerUserId)
            return (false, "This API key is already bound to another profile.");

        key.BindToUser(ownerUserId);
        await _apiKeys.UpdateAsync(key);
        return (true, "API key bound to this profile.");
    }

    public async Task<HashSet<string>> GetOwnedApiKeyNamesAsync(Guid ownerUserId)
    {
        var keys = await GetTenantScopedApiKeysAsync();
        return keys.Where(k => k.OwnerUserId == ownerUserId)
            .Select(k => k.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HashSet<string>> GetOwnedProviderNamesAsync(Guid ownerUserId)
    {
        var keys = (await GetTenantScopedApiKeysAsync())
            .Where(k => k.OwnerUserId == ownerUserId)
            .ToList();
        if (_tenantProvider?.TenantId is not { } tenantId)
            return [];
        var providers = await _providers.GetAllAsync(tenantId);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in keys.Select(k => k.PreferredProviderCode).Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            names.Add(code!.Trim());
            var provider = providers.FirstOrDefault(p => p.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
            if (provider is not null) names.Add(provider.Name);
        }
        return names;
    }

    /// <summary>
    /// Returns model usage for one profile owner. Ownership is resolved inside
    /// this service rather than being supplied by the page, so profile callers
    /// cannot accidentally fall back to the global usage view.
    /// </summary>
    public async Task<List<ModelUsageView>> GetOwnedModelUsageAsync(
        Guid ownerUserId, TimeRange range)
    {
        var ownedNames = await GetOwnedApiKeyNamesAsync(ownerUserId);
        if (ownedNames.Count == 0)
            return [];
        var ownedProviders = await GetOwnedProviderNamesAsync(ownerUserId);
        return ownedProviders.Count == 0
            ? await GetModelUsageAsync(range, allowedApiKeyNames: ownedNames)
            : await GetModelUsageAsync(range, allowedApiKeyNames: ownedNames, allowedProviderNames: ownedProviders);
    }

    /// <summary>Returns aggregate usage restricted to keys owned by one profile.</summary>
    public async Task<UsageStats> GetOwnedUsageStatsAsync(Guid ownerUserId, TimeRange range)
    {
        var ownedNames = await GetOwnedApiKeyNamesAsync(ownerUserId);
        if (ownedNames.Count == 0)
            return new UsageStats(0, 0, 0, 0, 0m);
        var ownedProviders = await GetOwnedProviderNamesAsync(ownerUserId);
        return ownedProviders.Count == 0
            ? await GetUsageStatsAsync(range, allowedApiKeyNames: ownedNames)
            : await GetUsageStatsAsync(range, allowedApiKeyNames: ownedNames, allowedProviderNames: ownedProviders);
    }

    /// <summary>Returns request logs restricted to keys owned by one profile.</summary>
    public async Task<List<LogEntryView>> GetOwnedLogsAsync(Guid ownerUserId, int count = 100)
    {
        var ownedNames = await GetOwnedApiKeyNamesAsync(ownerUserId);
        if (ownedNames.Count == 0)
            return [];
        return await GetFullLogsAsync(count, ownedNames);
    }

    /// <summary>
    /// One page of request-log rows for the profile page. Uses the scalar-only
    /// summary reader so browsing a long history never hydrates MessagesJson /
    /// ToolCallsJson; the full payload is fetched per row on demand via
    /// <see cref="GetOwnedLogByIdAsync"/>.
    /// </summary>
    public async Task<(List<LogSummaryView> Items, int TotalCount)> GetOwnedLogsPageAsync(
        Guid ownerUserId, int page, int pageSize)
    {
        var ownedNames = await GetOwnedApiKeyNamesAsync(ownerUserId);
        if (ownedNames.Count == 0)
            return ([], 0);

        var size = Math.Clamp(pageSize, 5, 200);
        var index = Math.Max(1, page);

        if (_summaryReader is not null)
        {
            var (items, total) = await _summaryReader.SearchSummariesPageAsync(
                ownedNames, (index - 1) * size, size);
            return (items.Select(ToSummaryView).ToList(), total);
        }

        // Fallback for hosts whose logger is not a summary reader (and unit
        // tests): page the full-log path in memory.
        var full = await GetFullLogsAsync(index * size, ownedNames);
        var totalCount = full.Count;
        var pageItems = full
            .Skip((index - 1) * size)
            .Take(size)
            .Select(l => new LogSummaryView(
                l.Id, l.Timestamp, l.Provider, l.Model, l.ApiKeyName, l.InputTokens, l.OutputTokens,
                l.TotalTokens, l.DurationMs, l.IsError, l.ErrorMessage, l.RouteKind))
            .ToList();
        return (pageItems, totalCount);
    }

    /// <summary>
    /// Full log detail for one row, guarded so a profile caller can only open
    /// logs produced by their own bound keys.
    /// </summary>
    public async Task<LogEntryView?> GetOwnedLogByIdAsync(Guid ownerUserId, Guid logId)
    {
        var ownedNames = await GetOwnedApiKeyNamesAsync(ownerUserId);
        if (ownedNames.Count == 0 || _tenantProvider?.TenantId is not { } tenantId)
            return null;
        var log = await _fullLogger.GetByIdAsync(logId);
        if (log is null || log.TenantId != tenantId || string.IsNullOrWhiteSpace(log.ApiKeyName)
            || !ownedNames.Contains(log.ApiKeyName, StringComparer.OrdinalIgnoreCase))
            return null;
        return MapLogEntry(log);
    }

    private static LogSummaryView ToSummaryView(RequestLogSummary s) =>
        new(s.Id, s.Timestamp, s.Provider, s.Model, s.ApiKeyName, s.InputTokens, s.OutputTokens,
            s.InputTokens + s.OutputTokens, (long)TimeSpan.FromTicks(s.DurationTicks).TotalMilliseconds,
            s.IsError, s.ErrorMessage, s.RouteKind);

    /// <summary>Lightweight row model for the paginated profile log list.</summary>
    public sealed record LogSummaryView(
        Guid Id,
        DateTimeOffset Timestamp,
        string Provider,
        string Model,
        string? ApiKeyName,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        long DurationMs,
        bool IsError,
        string? ErrorMessage,
        string RouteKind);

    // ── ChatGPT Codex subscription usage windows (Profile page) ──

    /// <summary>Snapshot older than this is surfaced as "stale" in the UI.</summary>
    private static readonly TimeSpan UsageStalenessThreshold = TimeSpan.FromHours(1);

    /// <summary>
    /// Latest ChatGPT Codex usage windows, scoped to the given profile owner:
    /// <c>Pinned</c> and <c>Pool</c> contain only windows of accounts the user's
    /// own keys pin (PreferredProviderCode). No other user's account snapshots
    /// are exposed. HasData=false when no owned account snapshot exists.
    /// </summary>
    public async Task<Endpoints.ProfileEndpoints.UsageWindowsResponse> GetUsageWindowsAsync(Guid ownerUserId)
    {
        var pinnedCodes = new List<string>();
        if (ownerUserId != Guid.Empty)
        {
            var owned = await GetTenantScopedApiKeysAsync();
            pinnedCodes.AddRange(owned
                .Where(k => k.OwnerUserId == ownerUserId && !string.IsNullOrWhiteSpace(k.PreferredProviderCode))
                .Select(k => k.PreferredProviderCode!.Trim()));
        }

        List<AccountUsageSnapshot> snapshots;
        Guid? tenantId = _tenantProvider?.TenantId;
        var repo = _usageSnapshots;
        if (repo is not null && tenantId is { } currentTenantId)
        {
            snapshots = await repo.GetByAccountCodesAsync(currentTenantId, pinnedCodes);
        }
        else
        {
            snapshots = [];
        }

        Endpoints.ProfileEndpoints.UsageWindowDto ToDto(AccountUsageSnapshot s) =>
            new(s.AccountCode,
                s.WindowKind == UsageWindowKind.Primary ? "5h" : "week",
                Math.Round(s.UsedPercent, 1),
                s.WindowMinutes,
                s.ResetsAtUtc,
                s.UpdatedAtUtc,
                s.PlanType,
                DateTimeOffset.UtcNow - s.UpdatedAtUtc > UsageStalenessThreshold);

        var pinned = snapshots
            .Where(s => pinnedCodes.Contains(s.AccountCode, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.WindowKind)
            .Select(ToDto)
            .ToList();

        var pool = snapshots
            .GroupBy(s => s.AccountCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Endpoints.ProfileEndpoints.UsageAccountDto(
                g.Key,
                g.OrderBy(s => s.WindowKind).Select(ToDto).ToList()))
            .ToList();

        // Per-pinned-account quota state. Codex accounts are classified from the
        // rolling snapshots; Antigravity (gemini-accN) accounts carry the
        // subscription state the gateway owns — connection status and the quota
        // cooldown the broker reported — because the upstream exposes no
        // usage-window feed for them.
        var accounts = new List<Endpoints.ProfileEndpoints.QuotaAccountDto>();
        var allProviders = tenantId is { } providerTenantId
            ? await _providers.GetAllAsync(providerTenantId)
            : [];
        foreach (var code in pinnedCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            var kind = ClassifyQuotaKind(code);
            var provider = allProviders.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            ProviderAccount? account = null;
            if (_providerAccounts is not null && tenantId is { } tid)
                account = await _providerAccounts.GetByCodeAsync(tid, code);

            var accountWindows = snapshots
                .Where(s => s.AccountCode.Equals(code, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.WindowKind)
                .Select(ToDto)
                .ToList();

            accounts.Add(new Endpoints.ProfileEndpoints.QuotaAccountDto(
                AccountCode: code,
                Kind: kind,
                DisplayName: provider?.Name ?? account?.DisplayName ?? code,
                IsEnabled: account?.IsEnabled ?? provider?.IsEnabled ?? false,
                Status: account is not null
                    ? account.ConnectionStatus.ToString()
                    : accountWindows.Count > 0 ? "tracked" : "no-data",
                QuotaCooldownUntil: account?.CooldownUntil,
                LastFailureClass: account?.LastFailureClass,
                LastFailureAt: account?.LastFailureAt,
                LastSuccessAt: account?.LastSuccessAt,
                TokenExpiresAt: account?.TokenExpiresAt,
                Windows: accountWindows,
                HasSnapshots: accountWindows.Count > 0));
        }

        return new Endpoints.ProfileEndpoints.UsageWindowsResponse(
            HasData: snapshots.Count > 0,
            Pinned: pinned,
            Pool: pool,
            Accounts: accounts);
    }

    /// <summary>
    /// Classifies a pinned provider/account code exactly the way chat routing
    /// does: <c>chatgpt-accN</c> = ChatGPT Codex subscription,
    /// <c>gemini-accN</c> = Gemini (Antigravity) subscription behind the broker.
    /// </summary>
    internal static string ClassifyQuotaKind(string code)
        => code.StartsWith("chatgpt-acc", StringComparison.OrdinalIgnoreCase) ? "codex"
           : code.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase) ? "antigravity"
           : "other";

    public async Task<(Guid Id, string PlainTextKey)> CreateApiKeyAsync(string name, List<Guid> modelIds,
        string? providerCode = null, bool allowProviderFallback = false)
    {
        var plainKey = Domain.Services.ApiKeyHasher.GenerateApiKey();
        var hash = Domain.Services.ApiKeyHasher.Hash(plainKey);
        var key = ApiKey.Create(name, hash, ApiKeyHasher.ExtractPrefix(plainKey));

        if (modelIds.Count > 0)
        {
            var allModels = await _models.GetAllAsync();
            var selected = allModels.Where(m => modelIds.Contains(m.Id)).ToList();
            if (providerCode is not null)
            {
                var providers = await _providers.GetAllAsync();
                var provider = providers.FirstOrDefault(p => p.IsEnabled
                    && p.Code.Equals(providerCode.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Provider '{providerCode}' is unavailable.");
                if (selected.Count != modelIds.Distinct().Count())
                    throw new InvalidOperationException("One or more selected models do not exist.");
                var mismatched = selected.FirstOrDefault(m => m.ProviderId != provider.Id);
                if (mismatched is not null)
                    throw new InvalidOperationException(
                        $"Model '{mismatched.Code}' does not belong to provider '{provider.Code}'.");
                key.PreferredProviderCode = provider.Code;
                key.PreferredProviderAccountId = null;
                if (_providerAccounts is not null && _tenantProvider?.TenantId is { } tenantId)
                {
                    var account = await _providerAccounts.GetByCodeAsync(tenantId, provider.Code);
                    if (account is not null)
                        key.PreferredProviderAccountId = account.Id;
                }
                key.AllowProviderFallback = allowProviderFallback;
            }
            foreach (var modelId in modelIds)
            {
                var model = allModels.FirstOrDefault(m => m.Id == modelId);
                if (model is not null)
                    key.AllowedModels.Add(model);
            }
        }

        await _apiKeys.AddAsync(key);
        return (key.Id, plainKey);
    }

    public async Task ToggleApiKeyAsync(Guid id)
    {
        var keys = await _apiKeys.GetAllAsync();
        var key = keys.FirstOrDefault(k => k.Id == id);
        if (key is null) return;
        if (key.IsActive) key.Deactivate(); else key.Activate();
        await _apiKeys.UpdateAsync(key);
    }

    public async Task RenameApiKeyAsync(Guid id, string newName)
    {
        var keys = await _apiKeys.GetAllAsync();
        var key = keys.FirstOrDefault(k => k.Id == id);
        if (key is null) return;
        key.Rename(newName);
        await _apiKeys.UpdateAsync(key);
    }

    public async Task UpdateApiKeyRateLimitsAsync(Guid keyId, int? rpm, int? tpm, int? maxConcurrent)
    {
        var keys = await _apiKeys.GetAllAsync();
        var key = keys.FirstOrDefault(k => k.Id == keyId);
        if (key is null) return;
        key.RateLimitRpm = rpm;
        key.RateLimitTpm = tpm;
        key.RateLimitMaxConcurrent = maxConcurrent;
        await _apiKeys.UpdateAsync(key);
    }

    /// <summary>
    /// Pin an API key's traffic to a specific provider, or clear the pin.
    /// </summary>
    /// <param name="providerCode">Provider code, or null for default routing.</param>
    public async Task UpdateApiKeyPreferredProviderAsync(Guid keyId, string? providerCode)
    {
        var keys = await _apiKeys.GetAllAsync();
        var key = keys.FirstOrDefault(k => k.Id == keyId);
        if (key is null) return;
        key.PreferredProviderCode = string.IsNullOrWhiteSpace(providerCode) ? null : providerCode.Trim();
        key.PreferredProviderAccountId = null;
        if (key.PreferredProviderCode is not null
            && _providerAccounts is not null
            && _tenantProvider?.TenantId is { } tenantId)
        {
            var account = await _providerAccounts.GetByCodeAsync(tenantId, key.PreferredProviderCode);
            if (account is not null)
                key.PreferredProviderAccountId = account.Id;
        }
        await _apiKeys.UpdateAsync(key);
    }

    /// <summary>
    /// Atomically validates and persists provider pin, fallback policy, and
    /// model scope. Models from another provider are rejected.
    /// </summary>
    public async Task UpdateApiKeyRoutingAsync(Guid keyId, string providerCode,
        bool allowProviderFallback, List<Guid> modelIds)
    {
        if (string.IsNullOrWhiteSpace(providerCode))
            throw new InvalidOperationException("A provider pin is required.");

        var providers = await _providers.GetAllAsync();
        var provider = providers.FirstOrDefault(p => p.IsEnabled
            && p.Code.Equals(providerCode.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerCode}' is unavailable.");

        var allModels = await _models.GetAllAsync();
        var selected = allModels.Where(m => modelIds.Contains(m.Id)).ToList();
        if (selected.Count != modelIds.Distinct().Count())
            throw new InvalidOperationException("One or more selected models do not exist.");
        var mismatched = selected.FirstOrDefault(m => m.ProviderId != provider.Id);
        if (mismatched is not null)
            throw new InvalidOperationException(
                $"Model '{mismatched.Code}' does not belong to provider '{provider.Code}'.");
        if (selected.Count == 0)
            throw new InvalidOperationException("Select at least one model for the pinned provider.");

        var keys = await _apiKeys.GetAllAsync();
        var key = keys.FirstOrDefault(k => k.Id == keyId)
            ?? throw new InvalidOperationException("API key was not found.");
        key.PreferredProviderCode = provider.Code;
        key.PreferredProviderAccountId = null;
        if (_providerAccounts is not null && _tenantProvider?.TenantId is { } tenantId)
        {
            var account = await _providerAccounts.GetByCodeAsync(tenantId, provider.Code);
            if (account is not null)
                key.PreferredProviderAccountId = account.Id;
        }
        key.AllowProviderFallback = allowProviderFallback;
        key.AllowedModels.Clear();
        foreach (var model in selected)
            key.AllowedModels.Add(model);
        await _apiKeys.UpdateAsync(key);
    }

    public async Task<List<ApiKeyRoutingHistoryView>> GetApiKeyRoutingHistoryAsync(
        string apiKeyName, int days = 30)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
        var logs = await _fullLogger.SearchAsync(apiKeyFilter: apiKeyName,
            from: from, maxResults: 10000);
        return logs
            .Where(l => string.Equals(l.ApiKeyName, apiKeyName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(l => l.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ApiKeyRoutingHistoryView(g.Key, g.Count(), g.Count(l => l.IsError),
                g.Min(l => l.Timestamp), g.Max(l => l.Timestamp)))
            .OrderByDescending(x => x.LastUsedAt)
            .ToList();
    }

    public async Task UpdateKeyModelsAsync(Guid keyId, List<Guid> modelIds)
    {
        var keys = await _apiKeys.GetAllAsync();
        var key = keys.FirstOrDefault(k => k.Id == keyId);
        if (key is null) return;

        key.AllowedModels.Clear();
        if (modelIds.Count > 0)
        {
            var allModels = await _models.GetAllAsync();
            foreach (var modelId in modelIds)
            {
                var model = allModels.FirstOrDefault(m => m.Id == modelId);
                if (model is not null)
                    key.AllowedModels.Add(model);
            }
        }
        await _apiKeys.UpdateAsync(key);
    }

    public async Task<(Guid Id, string PlainTextKey)> RotateApiKeyAsync(Guid id)
    {
        var keys = await _apiKeys.GetAllAsync();
        var key = keys.FirstOrDefault(k => k.Id == id);
        if (key is null) return (id, string.Empty);
        var plainKey = Domain.Services.ApiKeyHasher.GenerateApiKey();
        var hash = Domain.Services.ApiKeyHasher.Hash(plainKey);
        key.Rotate(hash, ApiKeyHasher.ExtractPrefix(plainKey));
        await _apiKeys.UpdateAsync(key);
        return (key.Id, plainKey);
    }

    public async Task DeleteApiKeyAsync(Guid id, Arkana.Infrastructure.Persistence.GatewayDbContext db)
    {
        var keys = await _apiKeys.GetAllAsync();
        var key = keys.FirstOrDefault(k => k.Id == id);
        if (key is null) return;
        db.ApiKeys.Remove(key);
        await db.SaveChangesAsync();
    }

    // ── Cost Chart (daily cost breakdown by model) ──────────

    public sealed record DailyCostPoint(
        DateOnly Date,
        string ModelCode,
        string ModelName,
        decimal Cost);

    public sealed record CostChartData(
        List<DateOnly> Dates,
        List<string> ModelCodes,
        Dictionary<string, string> ModelColors,
        decimal MaxCost,
        List<DailyCostPoint> Points);

    // Deterministic color palette for chart models (no JS, pure CSS)
    private static readonly string[] ChartPalette =
    [
        "#4ade80", // green      — deepseek-v4-flash
        "#f97316", // orange     — deepseek-v4-pro
        "#a78bfa", // violet     — glm-5.1
        "#2dd4bf", // teal       — glm-5
        "#f472b6", // pink       — kimi-k2.5
        "#38bdf8", // sky        — kimi-k2.6
        "#eab308", // yellow     — mimo-v2.5
        "#fb923c", // amber      — mimo-v2.5-pro
        "#818cf8", // indigo     — gpt-4o
        "#34d399", // emerald    — gpt-4o-mini
        "#c084fc", // purple     — claude-sonnet-4
        "#22d3ee", // cyan       — gemini
        "#f87171", // red        — ollama
        "#a3a3a3", // neutral    — fallback
    ];

    // Deterministic FNV-1a string hash for stable model→color mapping across renders.
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return (int)h;
        }
    }

    public async Task<CostChartData> GetCostChartDataAsync(int year, int month,
        string? filterModelCode = null, string? filterKeyPrefix = null)
    {
        var tzOffset = TzOffset;
        var from = new DateTimeOffset(year, month, 1, 0, 0, 0, tzOffset);
        var to = from.AddMonths(1);
        var usages = await _tracker.GetUsageAsync(from, to);

        // Filter by model if specified
        if (!string.IsNullOrEmpty(filterModelCode) && filterModelCode != "*")
            usages = usages.Where(u =>
                u.Model.Equals(filterModelCode, StringComparison.OrdinalIgnoreCase)).ToList();

        // Filter by API key if specified — TokenUsage.ApiKeyName is populated for both
        // EfCoreTokenTracker (maps TokenUsageEntity.ApiKeyName) and InMemoryTokenTracker.
        if (!string.IsNullOrEmpty(filterKeyPrefix) && filterKeyPrefix != "*")
        {
            usages = usages.Where(u =>
                u.ApiKeyName is not null &&
                (u.ApiKeyName.Contains(filterKeyPrefix, StringComparison.OrdinalIgnoreCase)
                 || u.ApiKeyName.StartsWith(filterKeyPrefix, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        // Deterministic model→color mapping so a model keeps the same color across
        // months/filters (comparison-safe; legend matches bars). Collision → next palette slot.
        var models = await _models.GetAllAsync();
        var modelCodesInData = usages
            .Select(u => u.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList();
        var modelColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paletteUsed = new bool[ChartPalette.Length];
        foreach (var mc in modelCodesInData)
        {
            var model = models.FirstOrDefault(m =>
                m.Code.Equals(mc, StringComparison.OrdinalIgnoreCase));
            var baseIdx = Math.Abs(StableHash(mc)) % ChartPalette.Length;
            int chosen = baseIdx;
            while (paletteUsed[chosen])
                chosen = (chosen + 1) % ChartPalette.Length;
            paletteUsed[chosen] = true;
            modelColors[mc] = ChartPalette[chosen];
        }

        // Build daily cost points
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var dates = Enumerable.Range(1, daysInMonth)
            .Select(d => new DateOnly(year, month, d))
            .ToList();

        var points = new List<DailyCostPoint>();
        foreach (var date in dates)
        {
            var dayStart = new DateTimeOffset(year, month, date.Day, 0, 0, 0, tzOffset);
            var dayEnd = dayStart.AddDays(1);
            var dayUsages = usages.Where(u =>
                u.Timestamp >= dayStart && u.Timestamp < dayEnd).ToList();

            var dayModels = dayUsages
                .Select(u => u.Model)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var mc in dayModels)
            {
                var modelObj = models.FirstOrDefault(m =>
                    m.Code.Equals(mc, StringComparison.OrdinalIgnoreCase));
                var modelName = modelObj?.Name ?? mc;
                var dayCost = dayUsages
                    .Where(u => u.Model.Equals(mc, StringComparison.OrdinalIgnoreCase))
                    .Sum(u => u.Cost);
                points.Add(new DailyCostPoint(date, mc, modelName, dayCost));
            }
        }

        var maxColumnCost = points.Count > 0
            ? points.GroupBy(p => p.Date).Max(g => g.Sum(p => p.Cost))
            : 1m;

        return new CostChartData(dates, modelCodesInData, modelColors, maxColumnCost, points);
    }

    // ── Usage Chart (time-range aware for Dashboard) ────────

    public sealed record UsageChartPoint(
        string PeriodLabel,
        string ModelCode,
        string ModelName,
        decimal Cost);

    public sealed record UsageChartResponse(
        List<string> PeriodLabels,
        List<string> ModelCodes,
        Dictionary<string, string> ModelColors,
        decimal MaxCost,
        List<UsageChartPoint> Points);

    public async Task<UsageChartResponse> GetUsageChartDataAsync(TimeRange range,
        string? providerFilter = null, string? apiKeyFilter = null)
    {
        var (from, to) = GetDateRange(range);
        var usages = await _tracker.GetUsageAsync(from, to);
        usages = ApplyFilters(usages, providerFilter, apiKeyFilter);
        var models = await _models.GetAllAsync();

        var modelCodesInData = usages
            .Select(u => u.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList();

        var modelColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var colorIdx = 0;
        foreach (var mc in modelCodesInData)
        {
            modelColors[mc] = ChartPalette[colorIdx % ChartPalette.Length];
            colorIdx++;
        }

        var labels = new List<string>();
        var points = new List<UsageChartPoint>();

        switch (range)
        {
            case TimeRange.Daily:
            {
                // Hourly breakdown (use DateTimeOffset throughout to preserve timezone)
                for (var h = 0; h <= 23; h++)
                {
                    var hourStart = new DateTimeOffset(from.Year, from.Month, from.Day, h, 0, 0, from.Offset);
                    var hourEnd = hourStart.AddHours(1);
                    var hourUsages = usages.Where(u =>
                        u.Timestamp >= hourStart && u.Timestamp < hourEnd).ToList();

                    labels.Add($"{h:D2}");

                    foreach (var mc in hourUsages.Select(u => u.Model).Distinct())
                    {
                        var cost = hourUsages
                            .Where(u => u.Model.Equals(mc, StringComparison.OrdinalIgnoreCase))
                            .Sum(u => u.Cost);
                        var modelObj = models.FirstOrDefault(m =>
                            m.Code.Equals(mc, StringComparison.OrdinalIgnoreCase));
                        points.Add(new UsageChartPoint(
                            $"{h:D2}", mc, modelObj?.Name ?? mc, cost));
                    }
                }
                break;
            }

            case TimeRange.Weekly:
            {
                // Daily breakdown for the week (use DateTimeOffset to preserve timezone)
                var offset = from.Offset;
                for (var d = 0; d < 7; d++)
                {
                    var day = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, offset).AddDays(d);
                    if (day > to) break;
                    var dayEnd = day.AddDays(1);
                    var dayUsages = usages.Where(u =>
                        u.Timestamp >= day && u.Timestamp < dayEnd).ToList();

                    labels.Add(day.ToString("ddd"));

                    foreach (var mc in dayUsages.Select(u => u.Model).Distinct())
                    {
                        var cost = dayUsages
                            .Where(u => u.Model.Equals(mc, StringComparison.OrdinalIgnoreCase))
                            .Sum(u => u.Cost);
                        var modelObj = models.FirstOrDefault(m =>
                            m.Code.Equals(mc, StringComparison.OrdinalIgnoreCase));
                        points.Add(new UsageChartPoint(
                            day.ToString("ddd"), mc, modelObj?.Name ?? mc, cost));
                    }
                }
                break;
            }

            case TimeRange.Monthly:
            {
                // Daily breakdown
                var offset = from.Offset;
                var daysInMonth = DateTime.DaysInMonth(from.Year, from.Month);
                for (var d = 1; d <= daysInMonth; d++)
                {
                    var day = new DateTimeOffset(from.Year, from.Month, d, 0, 0, 0, offset);
                    var dayEnd = day.AddDays(1);
                    if (day > to) break;
                    var dayUsages = usages.Where(u =>
                        u.Timestamp >= day && u.Timestamp < dayEnd).ToList();

                    labels.Add(day.ToString("MMM d"));

                    foreach (var mc in dayUsages.Select(u => u.Model).Distinct())
                    {
                        var cost = dayUsages
                            .Where(u => u.Model.Equals(mc, StringComparison.OrdinalIgnoreCase))
                            .Sum(u => u.Cost);
                        var modelObj = models.FirstOrDefault(m =>
                            m.Code.Equals(mc, StringComparison.OrdinalIgnoreCase));
                        points.Add(new UsageChartPoint(
                            day.ToString("MMM d"), mc, modelObj?.Name ?? mc, cost));
                    }
                }
                break;
            }

            case TimeRange.Yearly:
            {
                // Monthly breakdown
                var offset = from.Offset;
                for (var m = 1; m <= 12; m++)
                {
                    var monthStart = new DateTimeOffset(from.Year, m, 1, 0, 0, 0, offset);
                    var monthEnd = monthStart.AddMonths(1);
                    if (monthStart > to) break;
                    var monthUsages = usages.Where(u =>
                        u.Timestamp >= monthStart && u.Timestamp < monthEnd).ToList();

                    labels.Add(monthStart.ToString("MMM"));

                    foreach (var mc in monthUsages.Select(u => u.Model).Distinct())
                    {
                        var cost = monthUsages
                            .Where(u => u.Model.Equals(mc, StringComparison.OrdinalIgnoreCase))
                            .Sum(u => u.Cost);
                        var modelObj = models.FirstOrDefault(m =>
                            m.Code.Equals(mc, StringComparison.OrdinalIgnoreCase));
                        points.Add(new UsageChartPoint(
                            monthStart.ToString("MMM"), mc, modelObj?.Name ?? mc, cost));
                    }
                }
                break;
            }

            case TimeRange.All:
            {
                // Yearly breakdown
                var offset = TzOffset;
                var years = usages
                    .Where(u => u.Timestamp != DateTimeOffset.MinValue)
                    .Select(u => _userTime.ToLocal(u.Timestamp).Year)
                    .Distinct()
                    .Order()
                    .ToList();
                foreach (var year in years)
                {
                    var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, offset);
                    var yearEnd = yearStart.AddYears(1);
                    var yearUsages = usages.Where(u =>
                        u.Timestamp >= yearStart && u.Timestamp < yearEnd).ToList();

                    labels.Add(year.ToString());

                    foreach (var mc in yearUsages.Select(u => u.Model).Distinct())
                    {
                        var cost = yearUsages
                            .Where(u => u.Model.Equals(mc, StringComparison.OrdinalIgnoreCase))
                            .Sum(u => u.Cost);
                        var modelObj = models.FirstOrDefault(m =>
                            m.Code.Equals(mc, StringComparison.OrdinalIgnoreCase));
                        points.Add(new UsageChartPoint(
                            year.ToString(), mc, modelObj?.Name ?? mc, cost));
                    }
                }
                break;
            }
        }

        var maxColumnCost = points.Count > 0
            ? points.GroupBy(p => p.PeriodLabel).Max(g => g.Sum(p => p.Cost))
            : 1m;

        return new UsageChartResponse(labels, modelCodesInData, modelColors, maxColumnCost, points);
    }

    // ── Model filter list for chart ─────────────────────────

    public sealed record ModelFilterItem(string Code, string Name, string ProviderName);

    public async Task<List<ModelFilterItem>> GetModelFilterListAsync()
    {
        var models = await _models.GetAllAsync();
        return models
            .OrderBy(m => m.Provider.Name)
            .ThenBy(m => m.Name)
            .Select(m => new ModelFilterItem(m.Code, m.Name, m.Provider.Name))
            .ToList();
    }

    // ── Logs ─────────────────────────────────────────────────

    public sealed record LogView(
        string Provider, string Model,
        int InputTokens, int OutputTokens, int TotalTokens,
        decimal Cost, TimeSpan Duration, DateTimeOffset Timestamp);

    public async Task<List<LogView>> GetLogsAsync(int count = 100)
    {
        var usages = await _tracker.GetRecentUsageAsync(count);
        return usages.Select(u => new LogView(
            u.Provider, u.Model,
            u.InputTokens, u.OutputTokens, u.TotalTokens,
            u.Cost, u.Duration, u.Timestamp
        )).ToList();
    }

    // ── Full Request Logs ────────────────────────────────────

    public sealed record LogEntryView(
        Guid Id,
        string Provider,
        string Model,
        string? ApiKeyName,
        string? ViaMitmAgent,
        List<ChatMessageView> Messages,
        string? ResponseContent,
        List<ToolCallView>? ToolCalls,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        decimal Cost,
        long DurationMs,
        DateTimeOffset Timestamp,
        bool IsError,
        string? ErrorMessage,
        Guid? ResolvedProviderAccountId,
        string? ResolvedProviderAccountCode,
        string RouteKind);

    public sealed record ChatMessageView(
        string Role,
        string Content);

    public sealed record ToolCallView(
        string Id,
        string Type,
        string FunctionName,
        string FunctionArguments);

    public async Task<List<LogEntryView>> GetFullLogsAsync(int count = 100,
        IReadOnlySet<string>? allowedApiKeyNames = null)
    {
        var logs = await _fullLogger.GetRecentAsync(allowedApiKeyNames is null ? count : Math.Max(count * 10, 100));
        return logs
            .Where(log => allowedApiKeyNames is null ||
                (log.ApiKeyName is not null && allowedApiKeyNames.Contains(log.ApiKeyName)))
            .Take(count)
            .Select(MapLogEntry)
            .ToList();
    }

    public async Task<LogEntryView?> GetLogByIdAsync(Guid id)
    {
        var log = await _fullLogger.GetByIdAsync(id);
        return log is not null ? MapLogEntry(log) : null;
    }

    public async Task<List<LogEntryView>> SearchLogsAsync(
        string? providerFilter = null,
        string? modelFilter = null,
        string? apiKeyFilter = null,
        string? searchText = null,
        int maxResults = 100)
    {
        var logs = await _fullLogger.SearchAsync(providerFilter, modelFilter, apiKeyFilter, searchText, maxResults: maxResults);
        return logs.Select(MapLogEntry).ToList();
    }

    private static LogEntryView MapLogEntry(RequestLog log)
    {
        return new LogEntryView(
            log.Id,
            log.Provider,
            log.Model,
            log.ApiKeyName,
            log.ViaMitmAgent,
            log.Messages.Select(m => new ChatMessageView(m.Role, m.Content)).ToList(),
            log.ResponseContent,
            log.ToolCalls?.Select(tc => new ToolCallView(tc.Id, tc.Type, tc.FunctionName, tc.FunctionArguments)).ToList(),
            log.InputTokens,
            log.OutputTokens,
            log.TotalTokens,
            log.Cost,
            (long)log.Duration.TotalMilliseconds,
            log.Timestamp,
            log.IsError,
            log.ErrorMessage,
            log.ResolvedProviderAccountId,
            log.ResolvedProviderAccountCode,
            log.RouteKind
        );
    }

    // ── Provider Health ───────────────────────────────────────

    public sealed record ProviderAccountHealthView(
        Guid? AccountId,
        string? AccountCode,
        string Classification,
        int Requests,
        int Errors,
        DateTimeOffset? LastSeen);

    public sealed record ProviderHealthView(
        Guid ProviderId,
        string Name,
        string Code,
        string? BaseUrl,
        bool IsEnabled,
        bool HasRecentActivity,
        int RecentRequests,
        int Errors,
        double SuccessRate,
        double AvgDurationMs,
        DateTimeOffset? LastSeen,
        string? LastErrorMessage,
        string Status,
        IReadOnlyList<ProviderAccountHealthView> Accounts); // "healthy", "degraded", "down", "inactive", "no-data"

    public async Task<List<ProviderHealthView>> GetProviderHealthAsync()
    {
        var providers = await _providers.GetAllAsync();
        // Provider Health is an all-time view. Keep this as a scalar-only
        // projection so the panel does not hydrate MessagesJson/ToolCallsJson
        // for the complete request-log history.
        var allLogs = _summaryReader is not null
            ? await _summaryReader.SearchSummariesAsync(maxResults: int.MaxValue)
            : (await _fullLogger.SearchAsync(maxResults: int.MaxValue))
                .Select(l => new RequestLogSummary(
                    l.Id, l.Provider, l.Model, l.ApiKeyName, null, null, "legacy", l.IsError, l.InputTokens, l.OutputTokens,
                    l.Duration.Ticks, l.Timestamp, l.ErrorMessage))
                .ToList();

        var result = new List<ProviderHealthView>();
        foreach (var provider in providers.OrderBy(p => p.Priority))
        {
            var providerLogs = allLogs
                .Where(l => l.Provider.Equals(provider.Code, StringComparison.OrdinalIgnoreCase)
                         || l.Provider.Equals(provider.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.Timestamp)
                .ToList();

            var total = providerLogs.Count;
            var errors = providerLogs.Count(l => l.IsError);
            var lastLog = providerLogs.FirstOrDefault();
            var lastErr = providerLogs.FirstOrDefault(l => l.IsError);

            var successRate = total > 0 ? (double)(total - errors) / total * 100 : 0;
            var avgDuration = total > 0
                ? providerLogs.Average(l => TimeSpan.FromTicks(l.DurationTicks).TotalMilliseconds)
                : 0;

            string status;
            if (!provider.IsEnabled)
                status = "inactive";
            else if (total == 0)
                status = "no-data";
            else if (successRate >= 95)
                status = "healthy";
            else if (successRate >= 75)
                status = "degraded";
            else
                status = "down";

            var configuredAccounts = _providerAccounts is not null && _tenantProvider?.TenantId is Guid tenantId
                ? await _providerAccounts.GetForProviderAsync(tenantId, provider.Id)
                : [];
            var attributedHealth = providerLogs
                .Where(l => l.ResolvedProviderAccountId.HasValue)
                .GroupBy(l => l.ResolvedProviderAccountId!.Value)
                .Select(group =>
                {
                    var latest = group.OrderByDescending(l => l.Timestamp).First();
                    return new ProviderAccountHealthView(
                        group.Key,
                        group.Select(l => l.ResolvedProviderAccountCode).FirstOrDefault(code => code is not null),
                        "attributed",
                        group.Count(),
                        group.Count(l => l.IsError),
                        latest.Timestamp);
                })
                .ToList();
            var accountHealth = attributedHealth
                .Concat(configuredAccounts
                    .Where(a => attributedHealth.All(h => h.AccountId != a.Id))
                    .Select(a => new ProviderAccountHealthView(a.Id, a.Code, "never", 0, 0, null)))
                .Concat(providerLogs
                    .Where(l => !l.ResolvedProviderAccountId.HasValue)
                    .GroupBy(l => l.RouteKind.Equals("legacy", StringComparison.OrdinalIgnoreCase) ? "legacy" : "unattributed")
                    .Select(group => new ProviderAccountHealthView(null, null, group.Key, group.Count(), group.Count(l => l.IsError), group.Max(l => l.Timestamp))))
                .OrderByDescending(a => a.LastSeen)
                .ToList();

            result.Add(new ProviderHealthView(
                provider.Id,
                provider.Name,
                provider.Code,
                provider.BaseUrl,
                provider.IsEnabled,
                total > 0,
                total,
                errors,
                Math.Round(successRate, 1),
                Math.Round(avgDuration, 0),
                lastLog?.Timestamp,
                lastErr?.ErrorMessage,
                status,
                accountHealth
            ));
        }

        return result;
    }

    // ── Provider CRUD ─────────────────────────────────────────

    public async Task<ProviderView> CreateProviderAsync(string name, string code,
        string? baseUrl, string? apiKey, int priority,
        decimal costIn, decimal costOut, int? maxTokens,
        string authMethod = "ApiKey", Guid? oAuthConfigId = null)
    {
        var tenantId = RequireTenant("create a provider");
        var duplicate = await _providers.GetByCodeAsync(code, tenantId);
        if (duplicate is not null)
            throw new InvalidOperationException($"Provider code '{code}' is already in use by '{duplicate.Name}'.");
        await EnsureOAuthConfigAsync(authMethod, oAuthConfigId);

        var provider = AiProvider.Create(
            name, code, priority,
            baseUrl: baseUrl,
            apiKeyPlaintext: apiKey,
            vault: apiKey is not null ? _vault : null,
            validator: baseUrl is not null ? _urlValidator : null,
            costPerInput: costIn,
            costPerOutput: costOut);
        provider.AssignTenant(tenantId);
        if (Enum.TryParse<AuthMethod>(authMethod, ignoreCase: true, out var am) && am == AuthMethod.OAuth && oAuthConfigId is { } cfg)
            provider.SetAuthMethod(AuthMethod.OAuth, cfg);
        await _providers.AddAsync(provider);
        return new ProviderView(provider.Id, provider.Name, provider.Code, provider.BaseUrl,
            provider.IsEnabled, provider.Priority, provider.AuthMethod.ToString(), provider.OAuthConfig?.ProviderCode,
            provider.CostPerInputToken, provider.CostPerOutputToken,
            provider.MaxTokensPerRequest, provider.CreatedAt, provider.OAuthConfigId);
    }

    public async Task UpdateProviderAsync(Guid id, string name, string code,
        string? baseUrl, string? apiKey, int priority,
        decimal costIn, decimal costOut, int? maxTokens,
        string authMethod = "ApiKey", Guid? oAuthConfigId = null)
    {
        var tenantId = RequireTenant("update a provider");
        var duplicate = await _providers.GetByCodeAsync(code, tenantId);
        if (duplicate is not null && duplicate.Id != id)
            throw new InvalidOperationException($"Provider code '{code}' is already in use by '{duplicate.Name}'.");
        await EnsureOAuthConfigAsync(authMethod, oAuthConfigId);

        var provider = await _providers.GetByIdAsync(id, tenantId);
        if (provider is null) return;

        provider.UpdateDetails(name, code, priority, costIn, costOut, maxTokens);

        if (baseUrl is not null || apiKey is not null)
            provider.UpdateCredentials(baseUrl, apiKey, vault: apiKey is not null ? _vault : null, validator: baseUrl is not null ? _urlValidator : null);

        if (Enum.TryParse<AuthMethod>(authMethod, ignoreCase: true, out var am) && am == AuthMethod.OAuth && oAuthConfigId is { } cfg)
            provider.SetAuthMethod(AuthMethod.OAuth, cfg);
        else if (Enum.TryParse<AuthMethod>(authMethod, ignoreCase: true, out var am2) && am2 == AuthMethod.ApiKey && provider.UsesOAuth)
            provider.SetAuthMethod(AuthMethod.ApiKey, null);

        await _providers.UpdateAsync(provider, tenantId);
    }

    private async Task EnsureOAuthConfigAsync(string authMethod, Guid? oAuthConfigId)
    {
        if (!Enum.TryParse<AuthMethod>(authMethod, ignoreCase: true, out var method)
            || method != AuthMethod.OAuth)
            return;

        if (oAuthConfigId is not { } configId || configId == Guid.Empty)
            throw new InvalidOperationException("An OAuth configuration must be selected.");

        if (await _oauthConfigs.GetByIdAsync(configId) is null)
            throw new InvalidOperationException("The selected OAuth configuration was not found.");
    }

    public async Task DeleteProviderAsync(Guid id)
    {
        var tenantId = _tenantProvider?.TenantId
            ?? throw new InvalidOperationException("Authenticated tenant is required to delete a provider.");
        var provider = await _providers.GetByIdAsync(id, tenantId);
        if (provider is null)
            return;

        if (_providerAccountDashboard is not null)
        {
            var deleted = await _providerAccountDashboard.DeleteForProviderAsync(
                id,
                actor: "dashboard",
                key: $"dashboard-provider-delete-{id:N}");
            if (deleted)
                return;
        }

        await _providers.DeleteAsync(id, tenantId);
    }

    // ── OAuth provider integration (FR-3 / P1-3) ─────────────

    public sealed record OAuthConfigView(Guid Id, string ProviderCode, string DisplayName, string GrantType, string? ClientIdPrefix);

    public async Task<List<OAuthConfigView>> GetOAuthConfigsAsync()
    {
        var list = await _oauthConfigs.GetAllAsync();
        return list.Select(c => new OAuthConfigView(c.Id, c.ProviderCode, c.DisplayName, c.GrantType.ToString(),
            string.IsNullOrEmpty(c.ClientId) ? null : (c.ClientId.Length > 16 ? c.ClientId[..16] + "..." : c.ClientId))).ToList();
    }

    private async Task<AiProvider> RequireProviderByCodeAsync(string providerCode, string operation)
    {
        var tenantId = RequireTenant(operation);
        return await _providers.GetByCodeAsync(providerCode, tenantId)
            ?? throw new InvalidOperationException($"Provider '{providerCode}' was not found for the authenticated tenant.");
    }

    public async Task<OAuthConnectionStatus> GetOAuthStatusAsync(string providerCode)
    {
        await RequireProviderByCodeAsync(providerCode, "read OAuth status");
        return await _oauth.GetStatusAsync(providerCode);
    }

    public async Task<OAuthStartResult> StartOAuthAsync(string providerCode, string? baseUrl = null)
    {
        await RequireProviderByCodeAsync(providerCode, "start OAuth");
        var redirectBase = baseUrl ?? _nav.BaseUri.TrimEnd('/');
        return await _oauth.StartAsync(providerCode, redirectBase);
    }

    /// <summary>
    /// Drives a device-code OAuth flow to completion: polls the provider's device
    /// token endpoint and, once authorized, exchanges the code for tokens. Returns
    /// the resulting connection status string. Used by the dashboard for ChatGPT
    /// (and any DeviceCode-grant) providers whose UI has no redirect callback.
    /// </summary>
    public async Task<string> PollDeviceOAuthAsync(string providerCode)
    {
        await RequireProviderByCodeAsync(providerCode, "poll OAuth");
        var status = await _oauth.GetChatGptDeviceStatusAsync(providerCode);
        if (status is null) return "Pending";
        if (status.Ready)
        {
            // Device credentials are held and validated server-side. The
            // legacy arguments are intentionally empty and ignored.
            var result = await _oauth.CompleteChatGptDeviceAsync(status.State!);
            return result.Status.ToString();
        }
        return status.Status switch
        {
            "authorized" => "Pending",
            "error" => "Error",
            _ => "Pending",
        };
    }

    public async Task<string> PollOAuthAsync(string providerCode)
    {
        await RequireProviderByCodeAsync(providerCode, "poll OAuth");
        return (await _oauth.GetStatusAsync(providerCode)).Status.ToString();
    }

    public async Task DisconnectOAuthAsync(string providerCode)
    {
        await RequireProviderByCodeAsync(providerCode, "disconnect OAuth");
        await _oauth.DisconnectAsync(providerCode);
    }

    public sealed record OAuthAccountView(Guid Id, string ProviderCode, string? Label, string Status, DateTimeOffset? ExpiresAt);

    public async Task<List<OAuthAccountView>> GetOAuthAccountsAsync(Guid oauthConfigId)
    {
        if (_tenantProvider?.TenantId is not { } tenantId)
            return [];

        var all = await _providers.GetAllAsync(tenantId);
        var linked = all.Where(p => p.OAuthConfigId == oauthConfigId).ToList();
        if (_providerAccountDashboard is not null)
        {
            var deletedProviderIds = await _providerAccountDashboard.GetDeletedProviderIdsAsync();
            linked = linked.Where(p => !deletedProviderIds.Contains(p.Id)).ToList();
        }
        var views = new List<OAuthAccountView>();
        foreach (var p in linked)
        {
            var status = await _oauth.GetStatusAsync(p.Code);
            // Surface the provider's display name (the operator label) so the
            // account list shows the human-readable name instead of the code.
            views.Add(new OAuthAccountView(p.Id, p.Code, p.Name, status.Status.ToString(), status.ExpiresAt));
        }
        return views;
    }

    public async Task<string> CreateOAuthAccountAsync(Guid oauthConfigId, string oauthConfigCode, string label)
    {
        // Find the next unused account number. Counting rows is unsafe when an
        // account was deleted or when legacy rows have gaps (e.g. acc2 exists
        // while the row count is 1).
        var tenantId = RequireTenant("create an OAuth account");
        var usedCodes = await _providers.GetUsedCodesAsync(tenantId);
        var nextNum = 1;
        string code;
        do
        {
            code = $"{oauthConfigCode}-acc{nextNum++}";
        }
        while (usedCodes.Contains(code));
        var accountNumber = nextNum - 1;
        // Use the operator-supplied label as the provider display name so the
        // account is identifiable in the UI. Fall back to the auto name only when
        // no label was provided (e.g. programmatic callers).
        var name = string.IsNullOrWhiteSpace(label) ? $"{TitleCase(oauthConfigCode)} Account {accountNumber}" : label.Trim();

        // Create provider
        var provider = AiProvider.Create(name, code, 99,
            baseUrl: null, apiKeyPlaintext: null, vault: null, validator: null);
        provider.AssignTenant(tenantId);
        provider.SetAuthMethod(AuthMethod.OAuth, oauthConfigId);
        await _providers.AddAsync(provider);

        // ChatGPT/Codex accounts need a Codex model, not the Gemini default.
        var modelCodes = oauthConfigCode.Equals("chatgpt", StringComparison.OrdinalIgnoreCase)
            ? new[] { "gpt-5.5" }
            : new[] { "gemini-2.0-flash" };
        foreach (var mc in modelCodes)
        {
            var modelCode = oauthConfigCode.Equals("chatgpt", StringComparison.OrdinalIgnoreCase)
                ? mc
                : $"{mc}-acc{accountNumber}";
            var model = Model.Create(provider.Id, modelCode, modelCode, 0, 0);
            await _models.AddAsync(model);
        }

        // ProviderTargetPlanner requires a durable account identity before it
        // will route a per-account Gemini request. Keep the legacy AiProvider
        // row (used by this dashboard/OAuth flow) and create the corresponding
        // native account projection when the account-control repository is
        // available in the production DI graph.
        if (_providerAccounts is not null)
        {
            var account = ProviderAccount.Create(
                tenantId,
                provider.Id,
                code,
                name,
                ProviderAccountAuthOwnership.GatewayManagedOAuth,
                brokerKind: null);
            account.SetSupportedModels(modelCodes.Select(mc =>
                oauthConfigCode.Equals("chatgpt", StringComparison.OrdinalIgnoreCase)
                    ? mc
                    : $"{mc}-acc{accountNumber}"));
            await _providerAccounts.AddAsync(account);
        }

        return code;
    }

    private static string TitleCase(string s)
        => s.Length > 0 ? char.ToUpper(s[0], System.Globalization.CultureInfo.InvariantCulture) + s[1..] : s;

    // ── Model CRUD ────────────────────────────────────────────

    public sealed record ModelManageView(
        Guid Id, Guid ProviderId, string ProviderName,
        string Name, string Code, bool IsEnabled,
        decimal CostPerInputToken, decimal CostPerOutputToken,
        int? MaxTokensPerRequest);

    public async Task<ModelManageView> CreateModelAsync(Guid providerId, string name, string code,
        decimal costIn, decimal costOut, int? maxTokens)
    {
        var tenantId = RequireTenant("create a model");
        var provider = await _providers.GetByIdAsync(providerId, tenantId)
            ?? throw new InvalidOperationException("Provider not found.");
        var model = Model.Create(providerId, name, code, costIn, costOut, maxTokens);
        await _models.AddAsync(model);
        return new ModelManageView(model.Id, providerId, provider.Name,
            model.Name, model.Code, model.IsEnabled,
            model.CostPerInputToken, model.CostPerOutputToken, model.MaxTokensPerRequest);
    }

    public async Task UpdateModelAsync(Guid id, string name, string code,
        decimal costIn, decimal costOut, int? maxTokens)
    {
        var tenantId = RequireTenant("update a model");
        var model = await _models.GetByIdAsync(id, tenantId);
        if (model is null) return;
        model.UpdateDetails(name, code, costIn, costOut, maxTokens);
        await _models.UpdateAsync(model, tenantId);
    }

    public async Task DeleteModelAsync(Guid id)
    {
        var tenantId = RequireTenant("delete a model");
        var model = await _models.GetByIdAsync(id, tenantId);
        if (model is null) return;
        await _models.DeleteAsync(id, tenantId);
    }

    public async Task ToggleModelAsync(Guid id)
    {
        var tenantId = RequireTenant("update a model");
        var model = await _models.GetByIdAsync(id, tenantId);
        if (model is null) return;
        if (model.IsEnabled) model.Disable(); else model.Enable();
        await _models.UpdateAsync(model, tenantId);
    }

    // ── Model Sync ───────────────────────────────────────────

    public sealed record SyncResult(int Added, int Total);

    /// <summary>
    /// Known upstream base URLs for providers whose chat connectors hardcode their
    /// endpoint. These providers are seeded with a null BaseUrl (the connector owns
    /// the URL), so the sync path falls back to these to reach the /models endpoint.
    /// Keyed by AiProvider.Code (lowercase). Mirrors the endpoints in the *ChatService classes.
    /// </summary>
    private static readonly Dictionary<string, string> KnownProviderBaseUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"]    = "https://api.openai.com/v1",
            ["gemini"]    = "https://generativelanguage.googleapis.com/v1beta",
            ["anthropic"] = "https://api.anthropic.com/v1",
        };

    /// <summary>
    /// Calls GET {provider.BaseUrl}/models and upserts any new models into the DB.
    /// Returns count of newly added models and total models after sync.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown (and surfaced in the UI alert) when the provider has no reachable
    /// base URL at all, so a silent "0 models added" is never reported.
    /// </exception>
    public async Task<SyncResult> SyncModelsAsync(Guid providerId)
    {
        var tenantId = RequireTenant("sync provider models");
        var provider = await _providers.GetByIdAsync(providerId, tenantId);
        if (provider is null)
            throw new InvalidOperationException("Provider not found.");

        // ChatGPT / Codex accounts don't expose an OpenAI-compatible /models
        // endpoint (and require the per-account ChatGPT-Account-Id header + OAuth
        // token, not a plain bearer). Seed the known Codex-capable model set that
        // the connector forwards verbatim to /codex, so the dashboard has a usable
        // model list without a brittle HTTP discovery call.
        if (provider.Code.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase))
            return await SyncChatGptModelsAsync(provider, tenantId);

        // Resolve the upstream base URL. Prefer the stored BaseUrl; fall back to the
        // known endpoint for providers whose chat connector hardcodes the URL (seeded
        // with a null BaseUrl). A silent no-op here previously hid sync failures.
        var baseUrl = !string.IsNullOrEmpty(provider.BaseUrl)
            ? provider.BaseUrl.TrimEnd('/')
            : ResolveKnownProviderBaseUrl(provider.Code)
                ?? throw new InvalidOperationException(
                    $"Provider '{provider.Name}' has no BaseUrl configured and no known endpoint. " +
                    "Set its BaseUrl in the provider editor, then retry sync.");

        string? credential = null;
        if (provider.UsesOAuth)
        {
            credential = await _oauth.GetValidAccessTokenAsync(provider.Id);
            if (string.IsNullOrWhiteSpace(credential))
                throw new InvalidOperationException(
                    $"OAuth provider '{provider.Name}' is not connected or its access token is expired.");
        }
        else
        {
            try
            {
                credential = provider.DecryptApiKey(_vault);
            }
            catch
            {
                // Sealed key can't be decrypted (master key changed, etc.)
                // Continue without auth — some providers work without it
            }
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var isNativeGemini = provider.Code.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            || provider.Code.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase);
        var isGeminiAccount = provider.Code.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase);
        if (isGeminiAccount)
        {
            // Native OAuth accounts expose Gemini's OpenAI-compatible catalog.
            // Keep the stable account code while using the account endpoint.
            baseUrl = $"{baseUrl}/openai";
        }

        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        if (provider.UsesOAuth)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        else if (isNativeGemini && credential is not null)
            req.Headers.Add("x-goog-api-key", credential);
        else if (credential is not null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);

        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        // OpenAI-compatible providers return data[].id. Native Gemini returns
        // models[].name (usually prefixed with "models/"). Accept both shapes.
        var isOpenAiShape = root.TryGetProperty("data", out var dataArr)
            && dataArr.ValueKind == System.Text.Json.JsonValueKind.Array;
        var isNativeGeminiShape = root.TryGetProperty("models", out var nativeModels)
            && nativeModels.ValueKind == System.Text.Json.JsonValueKind.Array;
        if (!isOpenAiShape && !isNativeGeminiShape)
            return new SyncResult(0, 0);

        var remoteIds = new List<string>();
        var existingModels = await _models.GetByProviderIdAsync(providerId, tenantId);
        var existingCodes = new HashSet<string>(existingModels.Select(m => m.Code), StringComparer.OrdinalIgnoreCase);

        // Build cost hints from existing models by prefix family
        // e.g. deepseek-v4-flash -> prefix "deepseek" -> cost hints
        var costHints = new Dictionary<string, (decimal In, decimal Out)>(StringComparer.OrdinalIgnoreCase)
        {
            // Known model family defaults (used when no existing model provides a hint)
            ["minimax"] = (0.00000015m, 0.00000060m),
            ["qwen"]    = (0.00000040m, 0.00000160m),
            ["hy3"]     = (0.00000050m, 0.00000200m),
        };
        foreach (var em in existingModels)
        {
            var prefix = em.Code.Split('-', '-', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!costHints.ContainsKey(prefix) && (em.CostPerInputToken > 0 || em.CostPerOutputToken > 0))
                costHints[prefix] = (em.CostPerInputToken, em.CostPerOutputToken);
        }

        var added = 0;
        var remoteItems = isOpenAiShape ? dataArr.EnumerateArray() : nativeModels.EnumerateArray();
        foreach (var item in remoteItems)
        {
            var modelId = isOpenAiShape
                ? item.GetProperty("id").GetString()
                : item.GetProperty("name").GetString()?.Replace("models/", "", StringComparison.Ordinal);
            if (string.IsNullOrEmpty(modelId)) continue;
            remoteIds.Add(modelId);

            // Infer costs from model family prefix
            var costIn = 0m; var costOut = 0m;
            foreach (var (pfx, (ci, co)) in costHints)
            {
                if (modelId.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                {
                    costIn = ci; costOut = co;
                    break;
                }
            }

            if (existingCodes.Contains(modelId))
            {
                // Update existing models that have zero costs
                var existing = existingModels.FirstOrDefault(m =>
                    m.Code.Equals(modelId, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && existing.CostPerInputToken == 0 && existing.CostPerOutputToken == 0
                    && (costIn > 0 || costOut > 0))
                {
                    existing.UpdateDetails(existing.Name, existing.Code, costIn, costOut, existing.MaxTokensPerRequest);
                    await _models.UpdateAsync(existing, tenantId);
                }
                continue;
            }

            // Generate a friendly name from the id
            var name = ToModelName(modelId);

            var model = Model.Create(providerId, name, modelId, costIn, costOut, null);
            await _models.AddAsync(model);
            added++;
        }

        return new SyncResult(added, remoteIds.Count);
    }

    private static string? ResolveKnownProviderBaseUrl(string providerCode)
    {
        if (KnownProviderBaseUrls.TryGetValue(providerCode, out var exact))
            return exact;

        // Native OAuth accounts use stable account codes (gemini-accN) but share
        // Gemini's OpenAI-compatible model-discovery endpoint.
        return providerCode.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase)
            ? KnownProviderBaseUrls["gemini"]
            : null;
    }

    /// <summary>
    /// Seeds the known ChatGPT / Codex model set for a chatgpt-accN (or chatgpt)
    /// provider. The Codex backend has no OpenAI-compatible /models discovery and
    /// needs the per-account ChatGPT-Account-Id header + OAuth token, so we persist
    /// the stable list of models the connector forwards verbatim to /codex.
    /// </summary>
    private async Task<SyncResult> SyncChatGptModelsAsync(AiProvider provider, Guid tenantId)
    {
        // Static, known Codex-capable model ids (see ChatGptCodexChatService).
        // OpenAI rotates these, but this is the stable set ChatGPT subscriptions
        // expose through /codex today.
        // Ordered by preference. Authoritative Codex backend slugs come from
        // earendil-works/pi packages/ai/scripts/generate-models.ts (codexModels[]).
        // The old gpt-5-codex / gpt-5.1-codex / gpt-5.2-codex aliases are REJECTED
        // by /codex/responses, so they are intentionally excluded here; the connector
        // maps legacy client requests to a real slug.
        var known = new[]
        {
            "gpt-5.4",
            "gpt-5.4-mini",
            "gpt-5.5",
            "gpt-5.3-codex-spark",
            "gpt-5.6-luna",
            "gpt-5.6-sol",
            "gpt-5.6-terra",
        };

        var existing = await _models.GetByProviderIdAsync(provider.Id, tenantId);
        var existingCodes = new HashSet<string>(existing.Select(m => m.Code), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var modelId in known)
        {
            if (existingCodes.Contains(modelId)) continue;
            var name = ToModelName(modelId);
            var model = Model.Create(provider.Id, name, modelId, 0m, 0m, null);
            await _models.AddAsync(model);
            added++;
        }

        // Disable stale Codex model rows that are no longer valid backend slugs.
        // The old marketing aliases (gpt-5-codex / gpt-5.1-codex / gpt-5.2-codex) are
        // REJECTED by /codex/responses with HTTP 400; leaving them enabled makes
        // agents offer models that always 400. We therefore disable EVERY enabled
        // codex/gpt-5 row whose slug is not in `known` -- including the legacy
        // aliases. The server-side alias map in ChatGptCodexChatService still resolves
        // an old client request (gpt-5-codex -> real slug), but we must NOT surface
        // those dead slugs in the catalog that /v1/models advertises.
        var disabled = 0;
        foreach (var m in existing)
        {
            var isCodex = m.Code.Contains("codex", StringComparison.OrdinalIgnoreCase)
                || m.Code.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
            if (!isCodex) continue;
            if (m.IsEnabled
                && !known.Contains(m.Code, StringComparer.OrdinalIgnoreCase))
            {
                m.Disable();
                await _models.UpdateAsync(m, tenantId);
                disabled++;
            }
        }

        // NOTE: the connector hardcodes the /codex endpoint and never reads
        // provider.BaseUrl, so we intentionally do NOT mutate BaseUrl here (it would
        // require an SSRF validator in the sync path). The account is usable as-is.

        return new SyncResult(added, known.Length);
    }

    private static string ToModelName(string modelId)
    {
        // "deepseek-v4-flash" -> "DeepSeek V4 Flash"
        // "kimi-k2.7-code" -> "Kimi K2.7 Code"
        var parts = modelId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpper(parts[i][0], System.Globalization.CultureInfo.InvariantCulture) + parts[i][1..];
        }
        return string.Join(' ', parts);
    }

    // ── Key Pools ─────────────────────────────────────────────

    public sealed record KeyPoolView(
        Guid Id, Guid AiProviderId, string ProviderName, string ProviderCode,
        int ActiveIndex, long TotalRequests, long TotalRateLimits,
        bool IsActive, DateTimeOffset CreatedAt,
        int ActiveKeys, int TotalKeys);

    public sealed record KeyPoolEntryView(
        Guid Id, string Label, int Priority,
        bool IsActive, bool IsPermanentlyDisabled, bool IsInCooldown,
        string? LastErrorType, long RequestCount, long RateLimitCount,
        int ConsecutiveRateLimits, string[]? AllowedModels);

    public async Task<List<KeyPoolView>> GetKeyPoolsAsync(Guid tenantId)
    {
        var pools = await _keyPoolRepo.GetAllAsync(tenantId);
        var providers = await _providers.GetAllAsync();
        var providerDict = providers.ToDictionary(p => p.Id);

        return pools.Select(p =>
        {
            providerDict.TryGetValue(p.AiProviderId, out var prov);
            return new KeyPoolView(
                p.Id, p.AiProviderId,
                prov?.Name ?? "Unknown", prov?.Code ?? "",
                p.ActiveIndex, p.TotalRequests, p.TotalRateLimits,
                p.IsActive, p.CreatedAt,
                p.Entries.Count(e => e.IsActive && !e.IsPermanentlyDisabled),
                p.Entries.Count);
        }).ToList();
    }

    public async Task<(KeyPoolView? Pool, List<KeyPoolEntryView> Entries)?> GetKeyPoolDetailAsync(Guid poolId)
    {
        var pool = await _keyPoolRepo.GetByIdAsync(poolId);
        if (pool is null) return null;

        var providers = await _providers.GetAllAsync();
        var providerDict = providers.ToDictionary(p => p.Id);
        providerDict.TryGetValue(pool.AiProviderId, out var prov);

        var poolView = new KeyPoolView(
            pool.Id, pool.AiProviderId,
            prov?.Name ?? "Unknown", prov?.Code ?? "",
            pool.ActiveIndex, pool.TotalRequests, pool.TotalRateLimits,
            pool.IsActive, pool.CreatedAt,
            pool.Entries.Count(e => e.IsActive && !e.IsPermanentlyDisabled),
            pool.Entries.Count);

        var entries = pool.Entries.Select(e => new KeyPoolEntryView(
            e.Id, e.Label, e.Priority,
            e.IsActive, e.IsPermanentlyDisabled, e.IsInCooldown,
            e.LastErrorType, e.RequestCount, e.RateLimitCount,
            e.ConsecutiveRateLimits,
            e.AllowedModels
        )).OrderBy(e => e.Priority).ToList();

        return (poolView, entries);
    }

    public async Task<KeyPoolView?> CreateKeyPoolAsync(Guid providerId, Guid tenantId)
    {
        var existing = await _keyPoolRepo.GetByProviderAsync(providerId, tenantId);
        if (existing is not null) return null;

        var pool = ApiKeyPool.Create(providerId, tenantId);
        await _keyPoolRepo.CreateAsync(pool);

        var providers = await _providers.GetAllAsync();
        var providerDict = providers.ToDictionary(p => p.Id);
        providerDict.TryGetValue(providerId, out var prov);
        return new KeyPoolView(
            pool.Id, pool.AiProviderId,
            prov?.Name ?? "Unknown", prov?.Code ?? "",
            pool.ActiveIndex, pool.TotalRequests, pool.TotalRateLimits,
            pool.IsActive, pool.CreatedAt, 0, 0);
    }

    public async Task<bool> DeleteKeyPoolAsync(Guid poolId)
    {
        var pool = await _keyPoolRepo.GetByIdAsync(poolId);
        if (pool is null) return false;
        await _keyPoolRepo.DeleteAsync(pool.Id);
        return true;
    }

    public async Task<KeyPoolEntryView?> AddKeyToPoolAsync(Guid poolId, string plaintextKey, string label, int priority, int cooldownSeconds, string[]? allowedModels)
    {
        var pool = await _keyPoolRepo.GetByIdAsync(poolId);
        if (pool is null) return null;

        var sealedKey = _vault.Seal(plaintextKey);
        var entry = ApiKeyPoolEntry.Create(sealedKey!, label, priority, cooldownSeconds);
        if (allowedModels is { Length: > 0 })
            entry.AllowedModels = allowedModels;

        pool.Entries.Add(entry);
        await _keyPoolRepo.UpdateAsync(pool);

        return new KeyPoolEntryView(
            entry.Id, entry.Label, entry.Priority,
            entry.IsActive, entry.IsPermanentlyDisabled, entry.IsInCooldown,
            entry.LastErrorType, entry.RequestCount, entry.RateLimitCount,
            entry.ConsecutiveRateLimits,
            entry.AllowedModels);
    }

    public async Task<bool> RemoveKeyFromPoolAsync(Guid poolId, Guid entryId)
    {
        var pool = await _keyPoolRepo.GetByIdAsync(poolId);
        if (pool is null) return false;

        var entry = pool.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return false;

        pool.Entries.Remove(entry);
        await _keyPoolRepo.UpdateAsync(pool);
        return true;
    }

    public async Task<bool> TogglePoolKeyAsync(Guid poolId, Guid entryId)
    {
        var pool = await _keyPoolRepo.GetByIdAsync(poolId);
        if (pool is null) return false;

        var entry = pool.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return false;

        entry.IsActive = !entry.IsActive;
        await _keyPoolRepo.UpdateAsync(pool);
        return true;
    }

    public async Task<bool> UpdatePoolKeyModelsAsync(Guid poolId, Guid entryId, string[]? models)
    {
        var pool = await _keyPoolRepo.GetByIdAsync(poolId);
        if (pool is null) return false;

        var entry = pool.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return false;

        entry.AllowedModels = models;
        await _keyPoolRepo.UpdateAsync(pool);
        return true;
    }

    public async Task<bool> ResetPoolRotationAsync(Guid poolId)
    {
        var pool = await _keyPoolRepo.GetByIdAsync(poolId);
        if (pool is null) return false;

        pool.ActiveIndex = 0;
        await _keyPoolRepo.UpdateAsync(pool);
        return true;
    }

    // ── Agents (AI-ARKANA Agents module) ───────────────────────

    /// <summary>Default tenant scope used by the dashboard (mirrors KeyPools / Webhook / SLA admin endpoints).</summary>
    public static readonly Guid AgentDefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public sealed record AgentView(
        Guid Id, string Name, string Description, string ModelCode, string SystemPrompt,
        int MaxTokens, decimal Temperature, bool IsActive, DateTimeOffset CreatedAt,
        int TaskCount);

    public sealed record AgentTaskView(
        Guid Id, Guid AgentId, string Status, string Input, string? Output,
        string? ErrorMessage, long? DurationMs, int? TokenUsed,
        DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
        DateTimeOffset CreatedAt, Guid? ParentTaskId);

    public async Task<List<AgentView>> GetAgentsAsync()
    {
        var agents = await _agents.GetAllAgentsAsync(AgentDefaultTenantId);
        var tasks = agents.Count == 0
            ? new List<Arkana.Domain.Entities.AgentTask>()
            : (await Task.WhenAll(agents.Select(a => _agents.GetTasksByAgentAsync(a.Id)))).SelectMany(x => x).ToList();
        var counts = tasks.GroupBy(t => t.AgentId).ToDictionary(g => g.Key, g => g.Count());

        return agents.Select(a => new AgentView(
            a.Id, a.Name, a.Description, a.ModelCode, a.SystemPrompt,
            a.MaxTokens, a.Temperature, a.IsActive, a.CreatedAt,
            counts.GetValueOrDefault(a.Id, 0))).ToList();
    }

    public async Task<AgentView?> GetAgentAsync(Guid id)
    {
        var a = await _agents.GetAgentByIdAsync(id);
        if (a is null) return null;
        var taskCount = (await _agents.GetTasksByAgentAsync(a.Id)).Count;
        return new AgentView(a.Id, a.Name, a.Description, a.ModelCode, a.SystemPrompt,
            a.MaxTokens, a.Temperature, a.IsActive, a.CreatedAt, taskCount);
    }

    public async Task<Guid> CreateAgentAsync(string name, string? description,
        string systemPrompt, string modelCode, int maxTokens, decimal temperature)
    {
        var agent = Arkana.Domain.Entities.AgentDefinition.Create(
            AgentDefaultTenantId, name.Trim(), description?.Trim() ?? "",
            systemPrompt, modelCode, maxTokens, temperature);
        await _agents.AddAgentAsync(agent);
        return agent.Id;
    }

    public async Task UpdateAgentAsync(Guid id, string name, string? description,
        string systemPrompt, string modelCode, int maxTokens, decimal temperature, bool isActive)
    {
        var agent = await _agents.GetAgentByIdAsync(id)
            ?? throw new InvalidOperationException("Agent not found.");
        agent.Name = name.Trim();
        agent.Description = description?.Trim() ?? "";
        agent.SystemPrompt = systemPrompt;
        agent.ModelCode = modelCode;
        agent.MaxTokens = maxTokens;
        agent.Temperature = temperature;
        agent.IsActive = isActive;
        await _agents.UpdateAgentAsync(agent);
    }

    public async Task DeleteAgentAsync(Guid id)
    {
        await _agents.DeleteAgentAsync(id);
    }

    public async Task<List<AgentTaskView>> GetAgentTasksAsync(Guid agentId)
    {
        var tasks = await _agents.GetTasksByAgentAsync(agentId);
        return tasks.Select(t => new AgentTaskView(
            t.Id, t.AgentId, t.Status.ToString(), t.Input, t.Output,
            t.ErrorMessage, t.DurationMs, t.TokenUsed,
            t.StartedAt, t.CompletedAt, t.CreatedAt, t.ParentTaskId)).ToList();
    }

    public async Task<AgentTaskView> RunAgentAsync(Guid agentId, string input)
    {
        var task = await _agentOrchestrator.RunAsync(agentId, AgentDefaultTenantId, input);
        return ToTaskView(task);
    }

    public async Task<AgentTaskView> DelegateAgentTaskAsync(Guid parentTaskId, Guid childAgentId, string additionalInput)
    {
        var task = await _agentOrchestrator.DelegateAsync(parentTaskId, childAgentId, additionalInput);
        return ToTaskView(task);
    }

    private static AgentTaskView ToTaskView(Arkana.Domain.Entities.AgentTask t) => new(
        t.Id, t.AgentId, t.Status.ToString(), t.Input, t.Output,
        t.ErrorMessage, t.DurationMs, t.TokenUsed,
        t.StartedAt, t.CompletedAt, t.CreatedAt, t.ParentTaskId);
}
