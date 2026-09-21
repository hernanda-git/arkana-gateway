using Arkana.Domain.Entities;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Arkana.Infrastructure.Persistence;

/// <summary>
/// EF Core database context for the AI Gateway, managing API keys, AI providers,
/// models, token usage records, and request logs.
/// </summary>
public class GatewayDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayDbContext"/>.
    /// </summary>
    public GatewayDbContext(DbContextOptions<GatewayDbContext> options) : base(options) { }

    /// <summary>Registered API keys for gateway access.</summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    /// <summary>AI providers (OpenCode, OpenAI, Gemini, etc.).</summary>
    public DbSet<AiProvider> AiProviders => Set<AiProvider>();
    /// <summary>Typed provider accounts and broker-owned OAuth state.</summary>
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
    public DbSet<ProviderAccountOperation> ProviderAccountOperations => Set<ProviderAccountOperation>();
    /// <summary>AI models belonging to providers.</summary>
    public DbSet<Model> Models => Set<Model>();
    /// <summary>Persistent token usage records.</summary>
    public DbSet<TokenUsageEntity> TokenUsages => Set<TokenUsageEntity>();
    /// <summary>Persistent request/response log records.</summary>
    public DbSet<RequestLogEntity> RequestLogs => Set<RequestLogEntity>();
    /// <summary>Global rate limit defaults (single row, SEC-ARKANA-005).</summary>
    public DbSet<GlobalRateLimit> GlobalRateLimits => Set<GlobalRateLimit>();
    /// <summary>Dashboard admin users for Blazor UI auth.</summary>
    public DbSet<DashboardUser> DashboardUsers => Set<DashboardUser>();

    // ═══════════════════════════════════════════════════════════════
    //  COMPLIANCE (ENT-ARKANA-005)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Pre-built compliance policy templates.</summary>
    public DbSet<PolicyTemplate> PolicyTemplates => Set<PolicyTemplate>();

    // ═══════════════════════════════════════════════════════════════
    //  SCALE (Phase 6)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>SLA metrics per provider/model (Phase 6).</summary>
    public DbSet<SlaMetric> SlaMetrics => Set<SlaMetric>();

    /// <summary>Agent definitions (Phase 6).</summary>
    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();

    /// <summary>Agent task execution records (Phase 6).</summary>
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();

    /// <summary>Webhook registrations (Phase 5).</summary>
    public DbSet<Webhook> Webhooks => Set<Webhook>();

    /// <summary>API key pools for multi-key rotation.</summary>
    public DbSet<ApiKeyPool> ApiKeyPools => Set<ApiKeyPool>();

    /// <summary>Individual keys within a pool.</summary>
    public DbSet<ApiKeyPoolEntry> ApiKeyPoolEntries => Set<ApiKeyPoolEntry>();

    /// <summary>
    /// Latest ChatGPT Codex subscription usage per (account × window).
    /// Populated from upstream rate-limit reporting on every response.
    /// </summary>
    public DbSet<AccountUsageSnapshot> AccountUsageSnapshots => Set<AccountUsageSnapshot>();

    // ═══════════════════════════════════════════════════════════════
    //  OAUTH PROVIDER INTEGRATION (FR-3 / P1-3)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>OAuth client templates per platform (client secret sealed).</summary>
    public DbSet<OAuthProviderConfig> OAuthProviderConfigs => Set<OAuthProviderConfig>();

    /// <summary>Per-provider OAuth connection state (tokens sealed, tenant-scoped).</summary>
    public DbSet<ProviderOAuthToken> ProviderOAuthTokens => Set<ProviderOAuthToken>();

    /// <summary>In-flight authorization-code exchange state (PKCE/state, sealed).</summary>
    public DbSet<OAuthPendingFlow> OAuthPendingFlows => Set<OAuthPendingFlow>();

    // ═══════════════════════════════════════════════════════════════
    //  ENTERPRISE (Phase 4)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Customer organizations (ENT-ARKANA-001).</summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Pricing plans (ENT-ARKANA-002).</summary>
    public DbSet<Plan> Plans => Set<Plan>();

    /// <summary>Tenant-to-plan assignments (ENT-ARKANA-002).</summary>
    public DbSet<TenantPlan> TenantPlans => Set<TenantPlan>();

    /// <summary>Per-tenant budget tracking (ENT-ARKANA-003).</summary>
    public DbSet<TenantBudget> TenantBudgets => Set<TenantBudget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderAccountOperation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Nonce).HasMaxLength(64).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestFingerprint).HasMaxLength(256).IsRequired();
            e.Property(x => x.ExpectedVersion);
            e.Property(x => x.AuthorizationUrl).HasMaxLength(2048);
            e.Property(x => x.ResultJson).HasColumnType("text");
            e.Property(x => x.Slot).HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ProviderAccountId, x.Kind, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ProviderAccountId, x.Kind, x.State });
            e.HasIndex(x => x.Nonce).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ProviderAccountId, x.Kind })
                .HasFilter("\"State\" IN (0, 1)")
                .IsUnique();
            e.HasOne<ProviderAccount>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        // ── ApiKey ───────────────────────────────────────────
        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.KeyPrefix).HasMaxLength(12).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.PreferredProviderCode).HasMaxLength(64);
            e.Property(x => x.AccountRoutingMode).HasDefaultValue(AccountRoutingMode.Pool).IsRequired();
            e.HasIndex(x => x.PreferredProviderAccountId);
            e.HasIndex(x => x.KeyHash).IsUnique();

            // Multi-tenant scoping
            e.HasOne(x => x.Tenant).WithMany(t => t.ApiKeys)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TenantId);
            e.HasOne(x => x.OwnerUser).WithMany(u => u.ApiKeys)
                .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.OwnerUserId);
        });

        // ── GlobalRateLimit (single-row config) ─────────────
        modelBuilder.Entity<GlobalRateLimit>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DefaultRequestsPerMinute).HasDefaultValue(60);
            e.Property(x => x.DefaultTokensPerMinute).HasDefaultValue(1_000_000);
            e.Property(x => x.DefaultMaxConcurrent).HasDefaultValue(5);
            e.Property(x => x.Enabled).HasDefaultValue(true);
        });

        // ── AccountUsageSnapshot (latest Codex usage per account × window) ──
        modelBuilder.Entity<AccountUsageSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AccountCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.UsedPercent).HasColumnType("double precision");
            e.Property(x => x.PlanType).HasMaxLength(64);
            e.Property(x => x.TenantId).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.AccountProviderId, x.WindowKind }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.AccountCode });
            e.HasOne<Tenant>().WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── DashboardUser ─────────────────────────────────
        modelBuilder.Entity<DashboardUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(128).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(x => x.Role).HasMaxLength(64).HasDefaultValue("Admin");
            e.Property(x => x.Email).HasMaxLength(256);
            e.HasIndex(x => x.Username).IsUnique();

            // Multi-tenant scoping
            e.HasOne(x => x.Tenant).WithMany(t => t.DashboardUsers)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TenantId);
        });

        // ── PolicyTemplate ─────────────────────────────────
        modelBuilder.Entity<PolicyTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1024);
            e.Property(x => x.Config).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.IsActive);
        });

        // ── AiProvider ───────────────────────────────────────
        modelBuilder.Entity<AiProvider>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.ApiKey).HasMaxLength(1024);
            e.Property(x => x.AuthMethod).HasMaxLength(32).HasDefaultValue(AuthMethod.ApiKey).IsRequired();
            e.Property(x => x.OAuthConfigId);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasOne(x => x.OAuthConfig)
                .WithMany()
                .HasForeignKey(x => x.OAuthConfigId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.TenantId).IsRequired();
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        // ── ProviderAccount (broker-managed OAuth control plane) ───
        modelBuilder.Entity<ProviderAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            e.Property(x => x.AuthOwnership).HasConversion<int>().IsRequired();
            e.Property(x => x.BrokerKind).HasConversion<int>();
            e.Property(x => x.ConnectionStatus).HasConversion<int>().IsRequired();
            e.Property(x => x.BrokerCredentialId).HasMaxLength(256);
            e.Property(x => x.ExternalCredentialFileName).HasMaxLength(256);
            e.Property(x => x.BrokerInstanceId).HasMaxLength(128);
            e.Property<string>("BrokerInstanceIdNormalized")
                .HasMaxLength(128)
                .HasComputedColumnSql("lower(\"BrokerInstanceId\")", stored: true);
            e.Property(x => x.AuthDirectoryKey).HasMaxLength(128);
            e.Property(x => x.RoutingPrefix).HasMaxLength(128);
            e.Property(x => x.SupportedModels).HasMaxLength(2048);
            e.Property(x => x.LastFailureClass).HasMaxLength(128);
            e.Property(x => x.AuditActor).HasMaxLength(256);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            // Active broker slots represent physical CLIProxyAPI instances and
            // therefore are globally exclusive across tenants. Tombstones retain
            // history but do not hold a slot.
            e.HasIndex("BrokerInstanceIdNormalized")
                .IsUnique()
                .HasFilter("\"BrokerInstanceId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
            e.HasIndex(x => new { x.TenantId, x.AiProviderId, x.IsEnabled, x.ConnectionStatus });
            e.HasOne<AiProvider>().WithMany().HasForeignKey(x => x.AiProviderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Tenant>().WithMany(t => t.ProviderAccounts).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        // ── OAuthProviderConfig (client templates) ──────────
        modelBuilder.Entity<OAuthProviderConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.GrantType).HasMaxLength(32).IsRequired();
            e.Property(x => x.TokenEndpoint).HasMaxLength(1024).IsRequired();
            e.Property(x => x.AuthorizationEndpoint).HasMaxLength(1024);
            e.Property(x => x.DeviceAuthorizationEndpoint).HasMaxLength(1024);
            e.Property(x => x.ClientId).HasMaxLength(512).IsRequired();
            e.Property(x => x.SealedClientSecret).HasMaxLength(4096);
            e.Property(x => x.Scopes).HasMaxLength(512);
            e.HasIndex(x => x.ProviderCode).IsUnique();
        });

        // ── ProviderOAuthToken (per-connection, tenant-scoped) ─
        modelBuilder.Entity<ProviderOAuthToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SealedAccessToken).HasMaxLength(4096);
            e.Property(x => x.SealedRefreshToken).HasMaxLength(4096);
            e.Property(x => x.TokenType).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken().IsRequired();
            e.Property(x => x.CompletionFlowId);
            e.HasIndex(x => x.AiProviderId);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => new { x.TenantId, x.AiProviderId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.TenantId });
            e.HasOne(x => x.Provider)
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.AiProviderId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tenant)
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── OAuthPendingFlow (in-flight auth-code exchange, tenant-scoped) ─
        modelBuilder.Entity<OAuthPendingFlow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.State).HasMaxLength(128).IsRequired();
            e.Property(x => x.SealedCodeVerifier).HasMaxLength(4096);
            e.Property(x => x.SealedAuthorizationCode).HasMaxLength(4096);
            e.Property(x => x.SealedCompletionAccessToken).HasMaxLength(4096);
            e.Property(x => x.SealedCompletionRefreshToken).HasMaxLength(4096);
            e.Property(x => x.CompletionTokenType).HasMaxLength(32);
            e.Property(x => x.RedirectUri).HasMaxLength(1024).IsRequired();
            e.Property(x => x.ClaimedAt);
            e.Property(x => x.CompletionClaimedAt);
            e.Property(x => x.CompletionFailure).HasMaxLength(256);
            e.Property(x => x.CompletionFailedAt);
            e.HasIndex(x => x.State).IsUnique();
            e.HasIndex(x => x.TenantId);
            e.HasOne(x => x.Tenant)
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Model ────────────────────────────────────────────
        modelBuilder.Entity<Model>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.CostPerInputToken).HasColumnType("numeric(20,10)");
            e.Property(x => x.CostPerOutputToken).HasColumnType("numeric(20,10)");

            e.HasOne(x => x.Provider)
                .WithMany(p => p.Models)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.ProviderId, x.Code }).IsUnique();
            e.Ignore(x => x.TenantId);
            e.Ignore(x => x.Tenant);
        });

        // ── Many-to-many: ApiKey ↔ Model ────────────────────
        modelBuilder.Entity<ApiKey>()
            .HasMany(k => k.AllowedModels)
            .WithMany(m => m.AllowedByKeys)
            .UsingEntity(j => j.ToTable("ApiKeyModels"));

        // ══════════════════════════════════════════════════════
        //  TOKEN USAGE (persistent)
        // ══════════════════════════════════════════════════════

        modelBuilder.Entity<TokenUsageEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.Model).HasMaxLength(128).IsRequired();
            e.Property(x => x.ApiKeyName).HasMaxLength(256);
            e.Property(x => x.Cost).HasColumnType("numeric(20,10)");
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.Provider);
            e.HasIndex(x => x.Model);

            // Multi-tenant scoping
            e.HasIndex(x => x.TenantId);
        });

        // ══════════════════════════════════════════════════════
        //  REQUEST LOGS (persistent, JSONB for complex props)
        // ══════════════════════════════════════════════════════

        modelBuilder.Entity<RequestLogEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.Model).HasMaxLength(128).IsRequired();
            e.Property(x => x.ApiKeyName).HasMaxLength(256);
            e.Property(x => x.RequestedProviderCode).HasMaxLength(64);
            e.Property(x => x.RequestedProviderAccountCode).HasMaxLength(64);
            e.Property(x => x.ResolvedProviderAccountCode).HasMaxLength(64);
            e.Property(x => x.RouteKind).HasMaxLength(32).IsRequired();
            e.Property(x => x.MessagesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.ToolCallsJson).HasColumnType("jsonb");
            e.Property(x => x.ResponseContent).HasColumnType("text");
            e.Property(x => x.ErrorMessage).HasColumnType("text");
            e.Property(x => x.Cost).HasColumnType("numeric(20,10)");
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.Provider);
            e.HasIndex(x => x.Model);
            e.HasIndex(x => new { x.TenantId, x.ResolvedProviderAccountId });
            e.HasIndex(x => new { x.TenantId, x.Provider, x.Timestamp });
            e.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.ResolvedProviderAccountId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestedProviderAccountId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.NoAction);

            // Multi-tenant scoping
            e.HasIndex(x => x.TenantId);
        });

        // ══════════════════════════════════════════════════════
        //  SEED DATA
        // ══════════════════════════════════════════════════════

        var now = new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero);

        // ── Providers ────────────────────────────────────────
        var defaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        modelBuilder.Entity<AiProvider>().HasData(
            new { Id = Guid.Parse("a1000000-0000-0000-0000-000000000001"), Name = "OpenCode", Code = "opencode", BaseUrl = (string?)"http://localhost:8080", ApiKey = (string?)null, Priority = 0, IsEnabled = true, MaxTokensPerRequest = (int?)null, CostPerInputToken = 0.00000014m, CostPerOutputToken = 0.00000028m, CreatedAt = now, AuthMethod = AuthMethod.ApiKey, TenantId = defaultTenantId },
            new { Id = Guid.Parse("a1000000-0000-0000-0000-000000000002"), Name = "OpenAI", Code = "openai", BaseUrl = (string?)null, ApiKey = (string?)null, Priority = 1, IsEnabled = false, MaxTokensPerRequest = (int?)null, CostPerInputToken = 0.0000025m, CostPerOutputToken = 0.00001m, CreatedAt = now, AuthMethod = AuthMethod.ApiKey, TenantId = defaultTenantId },
            new { Id = Guid.Parse("a1000000-0000-0000-0000-000000000003"), Name = "Gemini", Code = "gemini", BaseUrl = (string?)null, ApiKey = (string?)null, Priority = 1, IsEnabled = false, MaxTokensPerRequest = (int?)null, CostPerInputToken = 0.00000125m, CostPerOutputToken = 0.000005m, CreatedAt = now, AuthMethod = AuthMethod.ApiKey, TenantId = defaultTenantId },
            new { Id = Guid.Parse("a1000000-0000-0000-0000-000000000004"), Name = "Anthropic", Code = "anthropic", BaseUrl = (string?)null, ApiKey = (string?)null, Priority = 2, IsEnabled = false, MaxTokensPerRequest = (int?)null, CostPerInputToken = 0.000003m, CostPerOutputToken = 0.000015m, CreatedAt = now, AuthMethod = AuthMethod.ApiKey, TenantId = defaultTenantId },
            new { Id = Guid.Parse("a1000000-0000-0000-0000-000000000005"), Name = "Ollama", Code = "ollama", BaseUrl = (string?)"http://localhost:11434", ApiKey = (string?)null, Priority = 2, IsEnabled = false, MaxTokensPerRequest = (int?)null, CostPerInputToken = 0m, CostPerOutputToken = 0m, CreatedAt = now, AuthMethod = AuthMethod.ApiKey, TenantId = defaultTenantId },
            new { Id = Guid.Parse("a1000000-0000-0000-0000-000000000006"), Name = "DeepSeek Direct", Code = "deepseek", BaseUrl = (string?)"https://api.deepseek.com/v1", ApiKey = (string?)null, Priority = 0, IsEnabled = true, MaxTokensPerRequest = (int?)null, CostPerInputToken = 0.00000014m, CostPerOutputToken = 0.00000028m, CreatedAt = now, AuthMethod = AuthMethod.ApiKey, TenantId = defaultTenantId }
        );

        // ── Models ───────────────────────────────────────────
        // OpenCode Go models (from opencode.ai/docs, verified 2026-06-06)
        var oid = Guid.Parse("a1000000-0000-0000-0000-000000000001");
        modelBuilder.Entity<Model>().HasData(
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000001"), ProviderId = oid, Name = "DeepSeek V4 Flash", Code = "deepseek-v4-flash", IsEnabled = true, CostPerInputToken = 0.00000014m, CostPerOutputToken = 0.00000028m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000002"), ProviderId = oid, Name = "DeepSeek V4 Pro", Code = "deepseek-v4-pro", IsEnabled = true, CostPerInputToken = 0.00000174m, CostPerOutputToken = 0.00000348m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000003"), ProviderId = oid, Name = "GLM-5.1", Code = "glm-5.1", IsEnabled = true, CostPerInputToken = 0.00000140m, CostPerOutputToken = 0.00000440m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000004"), ProviderId = oid, Name = "GLM-5", Code = "glm-5", IsEnabled = true, CostPerInputToken = 0.00000100m, CostPerOutputToken = 0.00000320m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000005"), ProviderId = oid, Name = "Kimi K2.5", Code = "kimi-k2.5", IsEnabled = true, CostPerInputToken = 0.00000060m, CostPerOutputToken = 0.00000300m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000006"), ProviderId = oid, Name = "Kimi K2.6", Code = "kimi-k2.6", IsEnabled = true, CostPerInputToken = 0.00000095m, CostPerOutputToken = 0.00000400m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000007"), ProviderId = oid, Name = "MiMo V2.5", Code = "mimo-v2.5", IsEnabled = true, CostPerInputToken = 0.00000014m, CostPerOutputToken = 0.00000028m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000008"), ProviderId = oid, Name = "MiMo V2.5 Pro", Code = "mimo-v2.5-pro", IsEnabled = true, CostPerInputToken = 0.00000174m, CostPerOutputToken = 0.00000348m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId }
        );

        // OpenAI models
        var openaiId = Guid.Parse("a1000000-0000-0000-0000-000000000002");
        modelBuilder.Entity<Model>().HasData(
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000011"), ProviderId = openaiId, Name = "GPT-4o", Code = "gpt-4o", IsEnabled = false, CostPerInputToken = 0.0000025m, CostPerOutputToken = 0.00001m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000012"), ProviderId = openaiId, Name = "GPT-4o Mini", Code = "gpt-4o-mini", IsEnabled = false, CostPerInputToken = 0.00000015m, CostPerOutputToken = 0.0000006m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000013"), ProviderId = openaiId, Name = "GPT-4.1", Code = "gpt-4.1", IsEnabled = false, CostPerInputToken = 0.000002m, CostPerOutputToken = 0.000008m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000014"), ProviderId = openaiId, Name = "O3", Code = "o3", IsEnabled = false, CostPerInputToken = 0.00001m, CostPerOutputToken = 0.00004m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000015"), ProviderId = openaiId, Name = "O4 Mini", Code = "o4-mini", IsEnabled = false, CostPerInputToken = 0.0000011m, CostPerOutputToken = 0.0000044m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId }
        );

        // Gemini models
        var geminiId = Guid.Parse("a1000000-0000-0000-0000-000000000003");
        modelBuilder.Entity<Model>().HasData(
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000021"), ProviderId = geminiId, Name = "Gemini 2.5 Pro", Code = "gemini-2.5-pro", IsEnabled = false, CostPerInputToken = 0.00000125m, CostPerOutputToken = 0.000005m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000022"), ProviderId = geminiId, Name = "Gemini 2.5 Flash", Code = "gemini-2.5-flash", IsEnabled = false, CostPerInputToken = 0.000000075m, CostPerOutputToken = 0.0000003m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000023"), ProviderId = geminiId, Name = "Gemini 2.0 Flash", Code = "gemini-2.0-flash", IsEnabled = false, CostPerInputToken = 0.0000001m, CostPerOutputToken = 0.0000004m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId }
        );

        // Anthropic models
        var anthId = Guid.Parse("a1000000-0000-0000-0000-000000000004");
        modelBuilder.Entity<Model>().HasData(
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000031"), ProviderId = anthId, Name = "Claude Sonnet 4", Code = "claude-sonnet-4", IsEnabled = false, CostPerInputToken = 0.000003m, CostPerOutputToken = 0.000015m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000032"), ProviderId = anthId, Name = "Claude Haiku 3.5", Code = "claude-haiku-3.5", IsEnabled = false, CostPerInputToken = 0.0000008m, CostPerOutputToken = 0.000004m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000033"), ProviderId = anthId, Name = "Claude Opus 4", Code = "claude-opus-4", IsEnabled = false, CostPerInputToken = 0.000015m, CostPerOutputToken = 0.000075m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId }
        );

        // Ollama models
        var ollamaId = Guid.Parse("a1000000-0000-0000-0000-000000000005");
        modelBuilder.Entity<Model>().HasData(
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000041"), ProviderId = ollamaId, Name = "Llama 3.1 8B", Code = "llama-3.1-8b", IsEnabled = false, CostPerInputToken = 0m, CostPerOutputToken = 0m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000042"), ProviderId = ollamaId, Name = "Llama 3.1 70B", Code = "llama-3.1-70b", IsEnabled = false, CostPerInputToken = 0m, CostPerOutputToken = 0m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000043"), ProviderId = ollamaId, Name = "Mistral", Code = "mistral", IsEnabled = false, CostPerInputToken = 0m, CostPerOutputToken = 0m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000044"), ProviderId = ollamaId, Name = "CodeLlama", Code = "codellama", IsEnabled = false, CostPerInputToken = 0m, CostPerOutputToken = 0m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId }
        );

        // DeepSeek Direct models
        var deepseekId = Guid.Parse("a1000000-0000-0000-0000-000000000006");
        modelBuilder.Entity<Model>().HasData(
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000051"), ProviderId = deepseekId, Name = "DeepSeek V4 Flash (Direct)", Code = "deepseek-v4-flash-direct", IsEnabled = true, CostPerInputToken = 0.00000014m, CostPerOutputToken = 0.00000028m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId },
            new { Id = Guid.Parse("b0000000-0000-0000-0000-000000000052"), ProviderId = deepseekId, Name = "DeepSeek V4 Pro (Direct)", Code = "deepseek-v4-pro-direct", IsEnabled = true, CostPerInputToken = 0.00000174m, CostPerOutputToken = 0.00000348m, MaxTokensPerRequest = (int?)null, CreatedAt = now, TenantId = defaultTenantId }
        );

        // ══════════════════════════════════════════════════════════
        //  ENTERPRISE ENTITIES (Phase 4)
        // ══════════════════════════════════════════════════════════

        // ── Tenant ─────────────────────────────────────────────
        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
        });

        // ── Plan ───────────────────────────────────────────────
        modelBuilder.Entity<Plan>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.Features).HasColumnType("text");
            e.Property(x => x.MonthlyPrice).HasColumnType("numeric(20,4)");
            e.HasIndex(x => x.Slug).IsUnique();
        });

        // ── TenantPlan (1:1 tenant -> plan assignment) ────────
        modelBuilder.Entity<TenantPlan>(e =>
        {
            e.HasKey(x => x.TenantId);
            e.HasOne(x => x.Tenant).WithOne(t => t.Plan)
                .HasForeignKey<TenantPlan>(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Plan).WithMany(p => p.TenantAssignments)
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── TenantBudget (1:1 tenant -> budget) ───────────────
        modelBuilder.Entity<TenantBudget>(e =>
        {
            e.HasKey(x => x.TenantId);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Tenant).WithOne(t => t.Budget)
                .HasForeignKey<TenantBudget>(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ══════════════════════════════════════════════════════════════
        //  PHASE 5 — WEBHOOKS
        // ══════════════════════════════════════════════════════════════

        modelBuilder.Entity<Webhook>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Url).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Secret).HasMaxLength(256);
            e.Property(x => x.RetryCount).HasDefaultValue(3);
            e.HasIndex(x => x.TenantId);
        });

        // ══════════════════════════════════════════════════════════════
        //  PHASE 6 — MULTI-AGENT ORCHESTRATION
        // ══════════════════════════════════════════════════════════════

        modelBuilder.Entity<AgentDefinition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1024);
            e.Property(x => x.SystemPrompt).HasColumnType("text");
            e.Property(x => x.ModelCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.Metadata).HasColumnType("text");
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<AgentTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Input).HasColumnType("text").IsRequired();
            e.Property(x => x.Output).HasColumnType("text");
            e.Property(x => x.ErrorMessage).HasColumnType("text");
            e.HasIndex(x => x.AgentId);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.Status);

            e.HasOne(x => x.Agent)
                .WithMany(a => a.Tasks)
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SlaMetric>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.ModelCode).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.ProviderCode, x.ModelCode, x.TenantId }).IsUnique();
            e.HasIndex(x => x.TenantId);
        });

        // ══════════════════════════════════════════════════════════════
        //  API KEY POOLS (multi-key rotation)
        // ══════════════════════════════════════════════════════════════

        modelBuilder.Entity<ApiKeyPool>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AiProviderId, x.TenantId }).IsUnique();
            e.HasOne(x => x.Provider).WithMany()
                .HasForeignKey(x => x.AiProviderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tenant).WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApiKeyPoolEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SealedKey).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Label).HasMaxLength(128);
            e.Property(x => x.AllowedModels)
                .HasConversion(
                    v => v != null && v.Length > 0
                        ? System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null)
                        : null,
                    v => string.IsNullOrEmpty(v)
                        ? null
                        : System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null));
            e.HasIndex(x => x.PoolId);
            e.HasOne(x => x.Pool).WithMany(p => p.Entries)
                .HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Seed: default tenant ──────────────────────────────
        modelBuilder.Entity<Tenant>().HasData(new
        {
            Id = defaultTenantId,
            Name = "Default Tenant",
            Slug = "default",
            IsActive = true,
            CreatedAt = now,
            Settings = (string?)null,
        });

        // ── Seed: pricing plans ─────────────────────────────
        var freePlanId = Guid.Parse("f0000000-0000-0000-0000-000000000001");
        var proPlanId = Guid.Parse("f0000000-0000-0000-0000-000000000002");
        var enterprisePlanId = Guid.Parse("f0000000-0000-0000-0000-000000000003");

        modelBuilder.Entity<Plan>().HasData(
            new
            {
                Id = freePlanId,
                Name = "Free",
                Slug = "free",
                MonthlyPrice = 0m,
                IncludedInputTokens = 0L,
                IncludedOutputTokens = 0L,
                MaxRequestsPerMinute = 10,
                MaxTokensPerMinute = 100_000,
                MaxConcurrent = 2,
                MaxApiKeys = 2,
                IsActive = true,
                Features = (string?)"{\"semantic_cache\":false,\"streaming\":true,\"embeddings\":false}",
            },
            new
            {
                Id = proPlanId,
                Name = "Pro",
                Slug = "pro",
                MonthlyPrice = 29.99m,
                IncludedInputTokens = 1_000_000L,
                IncludedOutputTokens = 500_000L,
                MaxRequestsPerMinute = 60,
                MaxTokensPerMinute = 1_000_000,
                MaxConcurrent = 5,
                MaxApiKeys = 10,
                IsActive = true,
                Features = (string?)"{\"semantic_cache\":true,\"streaming\":true,\"embeddings\":true}",
            },
            new
            {
                Id = enterprisePlanId,
                Name = "Enterprise",
                Slug = "enterprise",
                MonthlyPrice = 199.99m,
                IncludedInputTokens = 10_000_000L,
                IncludedOutputTokens = 5_000_000L,
                MaxRequestsPerMinute = 300,
                MaxTokensPerMinute = 10_000_000,
                MaxConcurrent = 25,
                MaxApiKeys = 100,
                IsActive = true,
                Features = (string?)"{\"semantic_cache\":true,\"streaming\":true,\"embeddings\":true}",
            }
        );

        // ── Seed: assign default tenant to Pro plan ─────────
        modelBuilder.Entity<TenantPlan>().HasData(new
        {
            TenantId = defaultTenantId,
            PlanId = proPlanId,
            StartsAt = now,
            EndsAt = (DateTimeOffset?)null,
        });

        // ── Seed: budget for default tenant ─────────────────
        modelBuilder.Entity<TenantBudget>().HasData(new
        {
            TenantId = defaultTenantId,
            MonthlyInputTokenCap = 1_000_000L,
            MonthlyOutputTokenCap = 500_000L,
            RemainingInputTokens = 1_000_000L,
            RemainingOutputTokens = 500_000L,
            PeriodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEnd = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1),
            RowVersion = 0u,
        });

        // ── Seed: compliance templates ──────────────────────
        var templateCreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        modelBuilder.Entity<PolicyTemplate>().HasData(
            new
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Name = "GDPR",
                Slug = "gdpr",
                Description = "EU General Data Protection Regulation — strict PII detection, no external engines, 365-day audit retention.",
                Config = """{"pii_detection":{"enabled":true,"patterns":["email","phone","name","address","ip"],"engine":"internal_only"},"data_retention":{"audit_days":365,"log_level":"full"},"external_engines":false,"masking":"full","consent_required":true}""",
                IsActive = true,
                IsBuiltin = true,
                CreatedAt = templateCreatedAt
            },
            new
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                Name = "PII Strict",
                Slug = "pii-strict",
                Description = "Aggressive PII masking across all engines — detects and masks all personally identifiable information.",
                Config = """{"pii_detection":{"enabled":true,"patterns":["email","phone","name","address","ssn","credit_card","ip","date_of_birth"],"engine":"all"},"masking":"full","external_engines":true,"consent_required":false,"content_filter":{"toxicity_threshold":0.7}}""",
                IsActive = true,
                IsBuiltin = true,
                CreatedAt = templateCreatedAt
            },
            new
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000003"),
                Name = "Compliance Audit",
                Slug = "compliance-audit",
                Description = "Log-only mode with full audit trail — tracks all requests without masking, for compliance review.",
                Config = """{"pii_detection":{"enabled":false},"masking":"none","audit":{"enabled":true,"log_requests":true,"log_responses":true,"log_metadata":true,"retention_days":730},"external_engines":true,"content_filter":null}""",
                IsActive = true,
                IsBuiltin = true,
                CreatedAt = templateCreatedAt
            },
            new
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000004"),
                Name = "Public Chatbot",
                Slug = "public-chatbot",
                Description = "Minimal guardrails for public-facing chatbots — topic allow-list, basic content filtering.",
                Config = """{"pii_detection":{"enabled":true,"patterns":["email","phone","credit_card"],"engine":"internal_only"},"masking":"partial","topic_allow_list":["general","support","faq"],"content_filter":{"toxicity_threshold":0.9,"block_profanity":true},"max_tokens_per_request":4096}""",
                IsActive = true,
                IsBuiltin = true,
                CreatedAt = templateCreatedAt
            },
            new
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000005"),
                Name = "Indonesia PII",
                Slug = "indonesia-pii",
                Description = "Indonesian-specific PII detection — NIK, NPWP, and national identity patterns with mandatory masking.",
                Config = """{"pii_detection":{"enabled":true,"patterns":["nik","npwp","email","phone","name","address"],"custom_patterns":[{"name":"nik","regex":"\\b\\d{16}\\b","description":"Nomor Induk Kependudukan"},{"name":"npwp","regex":"\\b\\d{2}\\.\\d{3}\\.\\d{3}\\.\\d{1}-\\d{3}\\.\\d{3}\\b","description":"Nomor Pokok Wajib Pajak"}],"engine":"internal_only"},"masking":"full","external_engines":false,"localization":"id-ID"}""",
                IsActive = true,
                IsBuiltin = true,
                CreatedAt = templateCreatedAt
            }
        );
    }
}
