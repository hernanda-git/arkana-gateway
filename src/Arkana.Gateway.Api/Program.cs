using Arkana.Application;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Gateway.Api.Components;
using Arkana.Gateway.Api.Endpoints;
using Scalar.AspNetCore;
using Arkana.Gateway.Api.Middleware;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure;
using Arkana.Gateway.Api.Auth;
using Arkana.Infrastructure.Services;
using Arkana.Infrastructure.Persistence;
using Arkana.ServiceDefaults;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddServiceDefaults();
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(
    postgresConnectionString: builder.Configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Port=5432;Database=arkana;Username=arkana;Password=arkana_dev",
    openCodeBaseUrl: builder.Configuration["ProviderOptions:OpenCode:BaseUrl"]
        ?? "https://opencode.ai/zen/go/v1",
    ssrfAllowHttp: builder.Environment.IsDevelopment()
        || builder.Configuration.GetValue<bool>("Ssrf:AllowHttp"),
    ssrfAllowPrivate: builder.Environment.IsDevelopment()
        || builder.Configuration.GetValue<bool>("Ssrf:AllowPrivateAddresses"));

// Dashboard — Blazor Server
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRazorPages(); // Login page
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<ProviderAccountDashboardFacade>();
builder.Services.AddScoped<AntigravityAccountService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserTimeService>();
// Dashboard circuits resolve their tenant through this (claim first; in the
// open-dashboard mode — AUTH=0 — the configured ADMIN_TENANT_ID).
builder.Services.AddScoped<DashboardTenantResolver>();
// Pin the tenant into each circuit's scope when it opens: MainLayout sits in the
// static (prerendered) part of the tree, so its pin never reaches the circuit.
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, Arkana.Gateway.Api.Auth.TenantCircuitHandler>();
builder.Services.AddSingleton<ActiveStreamCounter>();

// Agent orchestration (Phase 6)
builder.Services.AddScoped<AgentOrchestrator>(); // primary registration (see also Phase-6 block below; DI last-wins makes the duplicate harmless, kept single here)

// Admin user seeder — runs as background hosted service for startup resilience
builder.Services.AddHostedService<AdminUserSeederHostedService>();

// Multi-tenant provider — resolves current tenant from ApiKeyAuthMiddleware
// (API key requests) or claims (dashboard user sessions).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpContextTenantProvider>();
builder.Services.AddScoped<ITenantProvider>(sp => sp.GetRequiredService<HttpContextTenantProvider>());
builder.Services.AddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<HttpContextTenantProvider>());
builder.Services.AddSingleton<IAuthorizationHandler, AdminApiAuthorizationHandler>();

// Rate limit config service
builder.Services.AddSingleton<RateLimitConfigService>();
builder.Services.AddSingleton<IRateLimitConfigProvider>(
    sp => sp.GetRequiredService<RateLimitConfigService>());

// Dashboard authentication (SEC-ARKANA-003)
// Toggle via appsettings Auth:Enabled, env AUTH=***, or env Auth__Enabled=true
// Auth services and middleware are always registered so explicit policies remain
// effective even when the default dashboard policy allows anonymous access.
var authEnabled = builder.Configuration.GetValue<bool>("Auth:Enabled")
    || string.Equals(builder.Configuration["AUTH"], "1", StringComparison.OrdinalIgnoreCase);

// Data Protection — make antiforgery + cookie-ticket keys stable across
// container recreates (docker compose up -d --force-recreate, image rebuilds).
// Without an explicit application name the key ring is tied to the container's
// transient machine identity; a recreated container issues tokens it can't decrypt
// → AntiforgeryValidationException on every POST /login and Google OAuth callback →
// ModelState is Invalid → re-renders /login → the browser "renders forever".
// SEC-ARKANA-003: the named volume arkana-dev_dataprotection is mounted at this
// path by docker-compose.yml (gateway.volumes), so keys persist here across restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("arkana")
    .PersistKeysToFileSystem(new DirectoryInfo("/root/.aspnet/DataProtection-Keys"))
    .SetDefaultKeyLifetime(TimeSpan.FromDays(90));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // Rotate the cookie name to invalidate pre-fix/stale browser sessions.
        // This avoids rendering an authenticated but broken Blazor shell.
        options.Cookie.Name = ".Arkana.Auth.v2";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Mirror the TRANSPORT's own security: a `Secure` cookie is only emitted when
        // the client connection itself is HTTPS. Do NOT hardcode Always here — behind
        // the apache reverse proxy (TLS terminated at :443, forwarded to the container
        // over plain http://127.0.0.1:5011) and on the LAN http:// admin access, the
        // connection to the app is HTTP. A `Secure` cookie is silently DROPPED by
        // browsers on an http:// origin, which presents as a "successful" login that
        // immediately bounces back to /login with no error (SEC-ARKANA-003 symptom).
        // SameAsRequest keeps login working over http:// and still goes Secure the
        // moment the app is reached over real HTTPS.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminApi", policy =>
        policy.Requirements.Add(new AdminApiRequirement()));
    GatewayAuthorizationPolicyRegistration.AddOAuthManagement(options);

    // When auth is disabled the dashboard is open: the default policy must allow
    // anonymous users so <AuthorizeRouteView> never redirects to /login. When auth
    // is enabled the default policy requires an authenticated user (the cookie
    // middleware supplies it), so unauthenticated page hits redirect to /login.
    if (!authEnabled)
    {
        options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build();
        options.FallbackPolicy = options.DefaultPolicy;
    }
});
builder.Services.AddCascadingAuthenticationState();

// ── Google OAuth 2.0 dashboard login (Authorization Code + PKCE) ──
// Adds a SECOND authentication scheme ("Google") that issues the SAME cookie
// principal the dashboard already expects (see GoogleLoginEvents). It coexists
// with the password login, the ApiKey middleware and the Admin-Key API auth.
// Disabled unless GoogleLogin:Enabled (env GOOGLE_LOGIN_ENABLED=1) AND a client
// id/secret are configured. Defaults to off so existing deployments are
// unaffected until the operator provisions a Google Cloud OAuth app.
builder.Services.AddOptions<GoogleLoginOptions>()
    .BindConfiguration(GoogleLoginOptions.Section);
var googleOpts = builder.Configuration.GetSection(GoogleLoginOptions.Section)
    .Get<GoogleLoginOptions>() ?? new GoogleLoginOptions();
if (googleOpts.Enabled && !string.IsNullOrWhiteSpace(googleOpts.ClientId)
    && !string.IsNullOrWhiteSpace(googleOpts.ClientSecret))
{
    // The events class needs DI (repo, db, logger). Resolve it lazily from the
    // real service provider inside the handler so nothing is built at startup
    // (avoids BuildServiceProvider + the duplicate-singleton warning).
    builder.Services.AddAuthentication()
        .AddGoogle(options =>
        {
            options.ClientId = googleOpts.ClientId;
            options.ClientSecret = googleOpts.ClientSecret;
            // ASP.NET default callback path; proxy-friendly via UseForwardedHeaders.
            options.CallbackPath = "/signin-google";
            // PKCE (Authorization Code + PKCE) is the standard web flow.
            options.UsePkce = true;
            // We only need the identity claims in the cookie; no offline access.
            options.SaveTokens = false;
            options.Events = new OAuthEvents
            {
                OnCreatingTicket = ctx =>
                {
                    var svc = ctx.HttpContext.RequestServices
                        .GetRequiredService<GoogleLoginEvents>();
                    return svc.OnCreatingTicketAsync(ctx);
                }
            };
        });
    builder.Services.AddScoped<GoogleLoginEvents>();
}

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddOpenApi();

// ══════════════════════════════════════════════════════════════
//  PHASE 5 — MCP CLIENT + WEBHOOKS (DI in Gateway.Api for Clean Arch)
// ══════════════════════════════════════════════════════════════

// MCP Client — connects to external MCP servers via HTTP+SSE (JSON-RPC 2.0)
builder.Services.AddOptions<Arkana.Infrastructure.Mcp.McpClientOptions>()
    .BindConfiguration(Arkana.Infrastructure.Mcp.McpClientOptions.Section);
builder.Services.AddHttpClient<IMcpClient, Arkana.Infrastructure.Mcp.McpClient>();

// Webhooks
builder.Services.AddScoped<IWebhookRepository, Arkana.Infrastructure.Persistence.Repositories.WebhookRepository>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddSingleton<IWebhookDispatcher>(sp => sp.GetRequiredService<WebhookDispatcher>());
builder.Services.AddHostedService<WebhookDispatcher>();

// API Key Pool (multi-key rotation)
builder.Services.AddScoped<IApiKeyPoolRepository, Arkana.Infrastructure.Persistence.Repositories.ApiKeyPoolRepository>();
builder.Services.AddScoped<IApiKeyRotator, ApiKeyRotator>();

// ══════════════════════════════════════════════════════════════
//  PHASE 6 — MULTI-AGENT ORCHESTRATION + SLA MONITORING
// ══════════════════════════════════════════════════════════════

// Agent system
builder.Services.AddScoped<IAgentRepository, Arkana.Infrastructure.Persistence.Repositories.AgentRepository>();

// SLA monitoring (Phase 6) — recorder + repository now live in Domain
builder.Services.AddScoped<ISlaRepository, Arkana.Infrastructure.Persistence.Repositories.SlaRepository>();
builder.Services.AddScoped<Arkana.Domain.Services.SlaMonitor>();
builder.Services.AddScoped<ISlaMetricsRecorder>(sp => sp.GetRequiredService<Arkana.Domain.Services.SlaMonitor>());

var app = builder.Build();

// Report gemini-subscription/broker wiring gaps once, with the exact configuration key to set.
// These used to appear only as a 503 on the streaming path ("Gemini subscription streaming
// failed.") while non-streaming kept working, which is hard to trace back to configuration.
// The check itself must never be able to take the host down: a bad value is a warning, not a crash.
try
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GeminiBrokerConfig");
    var subscriptionOptions = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<Arkana.Infrastructure.AI.GeminiSubscriptionOptions>>().Value;
    var brokerOptions = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<Arkana.Infrastructure.Broker.CLIProxyManagementOptions>>().Value;
    var rawProviderId = app.Configuration["GeminiSubscription:ProviderId"];
    foreach (var problem in Arkana.Infrastructure.Broker.GeminiBrokerConfigValidation.Validate(subscriptionOptions, brokerOptions, rawProviderId))
        startupLogger.LogWarning("Gemini subscription/broker configuration: {Problem}", problem);
}
catch (Exception ex)
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GeminiBrokerConfig")
        .LogError(ex, "Gemini subscription/broker configuration check failed to run; the gateway continues.");
}


// Forwarded headers from Apache proxy (X-Forwarded-Proto, X-Forwarded-Host)
// Required for correct redirect URI generation in OAuth flow (Google requires HTTPS).
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
    // The reverse proxy (nginx/Apache) reaches Kestrel across the compose
    // network, so its source address is a deployment bridge IP — NOT loopback.
    // Without it in KnownNetworks, ASP.NET ignores X-Forwarded-Proto and the
    // built-in Google handler emits an http:// redirect_uri, causing
    // redirect_uri_mismatch at Google. Trust the compose subnets used by
    // production (172.19/16) and isolated staging (172.21/16).
    KnownIPNetworks =
    {
        System.Net.IPNetwork.Parse("172.19.0.0/16"),
        System.Net.IPNetwork.Parse("172.21.0.0/16")
    },
    // Trust all forwarded headers since the proxy is the sole entry point.
    ForwardLimit = null,
});

// Static web assets. Use the physical-file middleware for all assets because
// the /kb vault is runtime content and is not safe to route through the
// build-time static-assets manifest. That manifest can retain stale file sizes
// and throw ArgumentOutOfRangeException while serving the vault dependencies.
app.UseStaticFiles();

// Anti-forgery (required by Blazor Server interactive components)
app.UseAntiforgery();

// Cookie auth middleware is always active. The default policy is permissive
// when authEnabled is false, but explicit endpoint/page policies still fail closed.
app.UseAuthentication();
app.UseAuthorization();

// API auth / rate-limiting middleware — exempts Blazor dashboard paths
app.UseCors();
app.UseWhen(ctx =>
{
    var path = ctx.Request.Path.Value?.ToLowerInvariant() ?? "";
    return !path.StartsWith("/dashboard") &&
           !path.StartsWith("/cost") &&
           !path.StartsWith("/api-keys") &&
           !path.StartsWith("/providers") &&
           !path.StartsWith("/workflows") &&
           !path.StartsWith("/logs") &&
           !path.StartsWith("/settings") &&
           !path.StartsWith("/admin/users") &&
           !path.StartsWith("/admin/policy-templates") &&
           !path.StartsWith("/admin/webhooks") &&
           !path.StartsWith("/agents") &&
            !path.StartsWith("/agent-manager") &&
            !path.StartsWith("/key-pools") &&
           !path.StartsWith("/portal") &&
           !path.StartsWith("/oauth") &&
           !path.StartsWith("/admin/chatgpt") &&
           !path.StartsWith("/signin-google") &&
           !path.StartsWith("/auth/google/login") &&
           !path.StartsWith("/profile") &&
           !path.StartsWith("/docs") &&
           !path.StartsWith("/login") &&
           !path.StartsWith("/logout") &&
           !path.StartsWith("/privacy") &&
           // NOTE: "/v1/models" was excluded from auth here until 2026-08-06.
           // That made the full model catalog readable with no API key at all
           // (verified: HTTP 200 anonymously), and it also meant the endpoint
           // could not scope its response to the caller's allow-list because
           // no key had been resolved. It is now authenticated like the rest
           // of /v1/*, which is also what OpenAI-compatible clients expect.
           !path.StartsWith("/_framework") &&
           !path.StartsWith("/_content/") &&
           !path.StartsWith("/_blazor") &&
           path != "/" &&
           !path.StartsWith("/app.css") &&
           !path.StartsWith("/drag-scroll.js") &&
           !path.StartsWith("/js/") &&
           !path.StartsWith("/arkana") &&
           !path.StartsWith("/knowledge-base") &&
           !path.StartsWith("/knowledge-base/") &&
           !path.StartsWith("/kb") &&
           !path.StartsWith("/docs");
}, subApp =>
{
    subApp.UseMiddleware<ApiKeyAuthMiddleware>();
    subApp.UseMiddleware<RateLimitMiddleware>();
});
app.UseMiddleware<TokenTrackingMiddleware>();
app.UseMiddleware<GoogleUserScopeMiddleware>();
app.UseMiddleware<RoleAuthorizationMiddleware>(authEnabled);

// Admin API authentication (SEC-ARKANA-004). MUST come after UseAuthentication()
// above, otherwise context.User is not yet populated and a legitimately
// signed-in Admin would be rejected. Registered outside the UseWhen block
// because that block's predicate excludes several /admin page prefixes, and
// the management API must be gated regardless of those UI exemptions.
app.UseMiddleware<AdminAuthMiddleware>();

// API Endpoints
app.MapDefaultEndpoints();
app.MapChatEndpoints();
app.MapResponsesEndpoints();
app.MapEmbeddingsEndpoints();
app.MapImageGenerationEndpoints();
app.MapMcpServerEndpoints();
app.MapAdminEndpoints();
app.MapProviderAccountEndpoints();
app.MapProfileEndpoints();
app.MapOAuthEndpoints();
app.MapPolicyTemplateEndpoints();
app.MapWebhookEndpoints();
app.MapAgentEndpoints();
app.MapSlaEndpoints();
app.MapApiKeyPoolEndpoints();
app.MapRazorPages();

// Google OAuth — start the Authorization Code + PKCE flow. Anonymous (the
// browser hits this from the login page). The Google scheme handles the
// /signin-google callback itself and issues the cookie.
app.MapGet("/auth/google/login", async (HttpContext context) =>
{
    if (!googleOpts.Enabled
        || string.IsNullOrWhiteSpace(googleOpts.ClientId)
        || string.IsNullOrWhiteSpace(googleOpts.ClientSecret))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await context.ChallengeAsync(
        GoogleDefaults.AuthenticationScheme,
        // Let the scope middleware resolve the post-login destination from the
        // persisted role: Admin users land on the full gateway dashboard, while
        // non-Admin Google users are redirected to /profile.
        new AuthenticationProperties { RedirectUri = "/" });
}).AllowAnonymous();

// Logout — clears the dashboard auth cookie and returns to the login page.
// Registered as a minimal-API GET so a plain <a href="/logout"> (in the sidebar)
// signs the user out without needing a JS round-trip.
app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.Response.Redirect("/login");
}).AllowAnonymous();

// The knowledge-base vault is runtime content. Serve it through an explicit
// physical-file endpoint with higher route priority than the generated static
// asset endpoint, whose compressed-size metadata can be stale for /kb files.
var kbRoot = Path.GetFullPath(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "kb"));
app.MapGet("/kb/{**path}", (string? path) =>
{
    if (string.IsNullOrWhiteSpace(path)) return Results.NotFound();
    var relative = path.Replace('/', Path.DirectorySeparatorChar);
    var fullPath = Path.GetFullPath(Path.Combine(kbRoot, relative));
    if (!fullPath.StartsWith(kbRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest();
    return File.Exists(fullPath) ? Results.File(fullPath) : Results.NotFound();
}).AllowAnonymous().WithOrder(-100);

// Blazor Dashboard
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

// API docs (dev only)
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.Run();
