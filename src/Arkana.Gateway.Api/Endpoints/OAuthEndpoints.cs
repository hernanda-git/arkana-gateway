using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure.Broker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Server-orchestrated OAuth. The dashboard starts a flow per
/// provider template; the browser is redirected to the provider; the provider
/// redirects back to <c>/oauth/{providerCode}/callback</c>, where the gateway
/// exchanges the code for tokens server-side. Employees never see a secret.
///
/// SECURITY: All endpoints require authentication (dashboard session or API key)
/// EXCEPT the callback, which must be anonymous because the provider's browser
/// redirect hits it without a user session.
/// </summary>
public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        // SECURITY: All OAuth endpoints require authentication by default.
        // The callback endpoint overrides this with AllowAnonymous() since
        // the provider's browser redirect has no user session.
        var oauth = app.MapGroup("/oauth").RequireAuthorization("OAuthManagement");

        // ── List connectable OAuth provider templates + status (dashboard) ──
        oauth.MapGet("/configs", async (IOAuthProviderConfigRepository configs, IOAuthFlowService flow) =>
        {
            var cfgs = await configs.GetAllAsync();
            var views = new List<object>();
            foreach (var c in cfgs)
            {
                var status = await flow.GetStatusAsync(c.ProviderCode);
                views.Add(new
                {
                    c.Id,
                    c.ProviderCode,
                    c.DisplayName,
                    c.GrantType,
                    HasClientId = !string.IsNullOrEmpty(c.ClientId),
                    Status = status.Status.ToString(),
                    ExpiresAt = status.ExpiresAt,
                    HasRefreshToken = status.HasRefreshToken,
                    LastError = status.LastError,
                });
            }
            return Results.Ok(views);
        })
        .WithName("ListOAuthConfigs").WithTags("OAuth");

        // ── Start a flow for a provider (returns real authorize URL) ────────
        oauth.MapPost("/{providerCode}/start", async (string providerCode, HttpContext ctx, IOAuthFlowService flow, IConfiguration config) =>
        {
            if (!TryGetPublicOrigin(ctx, config, app.Environment.IsDevelopment(), out var redirectBase))
                return Results.Problem("OAuth public origin is not configured.", statusCode: 503);
            try
            {
                var result = await flow.StartAsync(providerCode, redirectBase);
                return Results.Ok(new
                {
                    result.ProviderCode,
                    result.DisplayName,
                    result.AuthorizationUrl,
                    result.State,
                    result.IsDeviceCode,
                    result.UserCode,
                    result.VerificationUrl,
                });
            }
            catch (InvalidOperationException)
            {
                return Results.BadRequest(new { error = "OAuth request could not be started." });
            }
        })
        .WithName("StartOAuth").WithTags("OAuth");

        // ── Provider redirect target (user-agent hits this; anonymous) ──────
        // SECURITY: This endpoint MUST remain anonymous because the OAuth provider
        // redirects the user's browser here directly (no user session cookie).
        // The state parameter provides CSRF protection instead.
        oauth.MapGet("/{providerCode}/callback", async (string providerCode, string? code, string? state, string? error, ProviderAccountDashboardFacade broker, IOAuthFlowService flow, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Pragma = "no-cache";
            if (!OAuthCallbackValidator.TryValidate(ctx.Request, code, state, error, out _))
                return Results.Content(CallbackHtml(false, "Authorization callback was invalid."), "text/html", statusCode: StatusCodes.Status400BadRequest);

            BrokerOAuthCallbackHandlingResult? brokerCallback;
            try
            {
                brokerCallback = await broker.TryHandleBrokerCallbackAsync(providerCode, code, state, error, ct);
            }
            catch (InvalidOperationException)
            {
                return Results.Content(CallbackHtml(false, "Authorization could not be routed."), "text/html", statusCode: StatusCodes.Status409Conflict);
            }
            catch (ArgumentException)
            {
                return Results.Content(CallbackHtml(false, "Authorization callback was invalid."), "text/html", statusCode: StatusCodes.Status400BadRequest);
            }
            catch (HttpRequestException)
            {
                return Results.Content(CallbackHtml(false, "Authorization could not be forwarded to the broker."), "text/html", statusCode: StatusCodes.Status502BadGateway);
            }

            if (brokerCallback is not null && brokerCallback.Handled)
            {
                var message = brokerCallback.Disposition == CLIProxyOAuthCallbackDisposition.Accepted
                    ? "Authorization received by the staging broker. You may close this tab and return to the gateway."
                    : "This authorization callback was already handled. Return to the gateway to see the broker status.";
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers.Pragma = "no-cache";
                return Results.Content(CallbackHtml(true, message), "text/html");
            }

            if (!string.IsNullOrWhiteSpace(error))
                return Results.Content(CallbackHtml(false, "Authorization failed."), "text/html", statusCode: StatusCodes.Status400BadRequest);

            var result = await flow.HandleCallbackAsync(providerCode, code!, state!, ct);
            var ok = result.Status == OAuthTokenStatus.Connected;
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Pragma = "no-cache";
            var html = ok
                ? CallbackHtml(true, "Authorization completed. You may close this tab and return to the gateway.")
                : CallbackHtml(false, "Authorization failed.");
            return Results.Content(html, "text/html");
        })
        .WithName("OAuthCallback").WithTags("OAuth")
        .AllowAnonymous(); // the provider's browser redirect hits this

        // ── Recovery for a claimed, already-exchanged callback ─────────────
        // The opaque state is the capability. Recovery never posts the consumed
        // authorization code again; it only finalizes sealed pending state.
        oauth.MapPost("/{providerCode}/recover", async (string providerCode, OAuthRecoveryRequest req, IOAuthFlowService flow) =>
        {
            try
            {
                var result = await flow.RecoverCallbackAsync(providerCode, req.State);
                return result.Status == OAuthTokenStatus.Connected
                    ? Results.Ok(new { status = result.Status.ToString() })
                    : Results.Conflict(new { error = result.ErrorMessage ?? "OAuth recovery is unavailable." });
            }
            catch (InvalidOperationException)
            {
                return Results.BadRequest(new { error = "OAuth recovery could not be completed." });
            }
        })
        .WithName("RecoverOAuthCallback").WithTags("OAuth")
        .AllowAnonymous();

        // ── Disconnect (dashboard) ─────────────────────────────────────────
        oauth.MapPost("/{providerCode}/disconnect", async (string providerCode, IOAuthFlowService flow) =>
        {
            await flow.DisconnectAsync(providerCode);
            return Results.Ok(new { providerCode, status = nameof(OAuthTokenStatus.Revoked) });
        })
        .WithName("DisconnectOAuth").WithTags("OAuth");

        // ── Status for a provider (dashboard polling) ───────────────────────
        oauth.MapGet("/{providerCode}/status", async (string providerCode, IOAuthFlowService flow) =>
        {
            var status = await flow.GetStatusAsync(providerCode);
            return Results.Ok(status);
        })
        .WithName("OAuthStatus").WithTags("OAuth");

        // ── ChatGPT / Codex device-code flow (server-driven) ──────────────
        // The gateway is a headless server, so we use OpenAI's device-code
        // grant. The admin hits /start (per account) to get a user_code, the
        // human enters it at the verification URL, and the gateway polls until
        // the token is issued. No localhost browser redirect required.

        oauth.MapPost("/chatgpt/start", async (ChatGptStartRequest req, IOAuthFlowService flow) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest(new { Error = "Account code is required (e.g. chatgpt-acc1)." });
            var result = await flow.StartChatGptDeviceAsync(req.Code);
            if (!result.Success)
                return Results.Problem(result.Error ?? "Failed to start ChatGPT device flow.");
            return Results.Ok(new
            {
                State = "", // client polls by account code below
                Code = req.Code,
                UserCode = result.UserCode,
                VerificationUrl = result.VerificationUrl,
                Message = $"Open {result.VerificationUrl} and enter code {result.UserCode}, then poll /oauth/chatgpt/poll?code={req.Code}"
            });
        })
        .WithName("ChatGptDeviceStart").WithTags("OAuth");

        oauth.MapGet("/chatgpt/poll", async (string code, IOAuthFlowService flow) =>
        {
            // Find the pending flow for this account code and poll it.
            var status = await flow.GetChatGptDeviceStatusAsync(code);
            if (status is null)
                return Results.NotFound(new { Error = $"No active device flow for {code}." });
            return Results.Ok(status);
        })
        .WithName("ChatGptDevicePollStatus").WithTags("OAuth");

        oauth.MapPost("/chatgpt/complete", async (ChatGptCompleteRequest req, IOAuthFlowService flow) =>
        {
            var result = await flow.CompleteChatGptDeviceAsync(req.State);
            return result.Status == OAuthTokenStatus.Connected
                ? Results.Ok(new { Success = true, Message = "ChatGPT account linked." })
                : Results.Problem(result.ErrorMessage ?? "ChatGPT linking failed.");
        })
        .WithName("ChatGptDeviceComplete").WithTags("OAuth");
    }

    private static string CallbackHtml(bool success, string message)
    {
        var color = success ? "#16a34a" : "#dc2626";
        var title = success ? "Authorization complete" : "Authorization failed";
        var mark = success ? "✓" : "✕";
        var sb = new System.Text.StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{title}</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:system-ui,Segoe UI,Roboto,sans-serif;background:#0f1115;color:#e5e7eb;");
        sb.Append("display:flex;align-items:center;justify-content:center;height:100vh;margin:0}");
        sb.Append(".card{background:#1a1d24;border:1px solid #2a2f3a;border-radius:12px;padding:32px 40px;max-width:420px;text-align:center}");
        sb.Append($".dot{{width:48px;height:48px;border-radius:50%;background:{color};margin:0 auto 16px;");
        sb.Append("display:flex;align-items:center;justify-content:center;color:#fff;font-size:24px}");
        sb.Append("h2{margin:0 0 8px;font-size:18px} p{margin:0;color:#9ca3af;font-size:14px}");
        sb.Append("</style>");
        sb.Append("<script>setTimeout(()=>{try{window.close()}catch(e){}},2000)</script>");
        sb.Append("</head>");
        sb.Append("<body><div class=\"card\">");
        sb.Append($"<div class=\"dot\">{mark}</div>");
        sb.Append($"<h2>{title}</h2><p>{System.Net.WebUtility.HtmlEncode(message)}</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    internal static bool TryGetPublicOrigin(
        HttpContext context,
        IConfiguration configuration,
        bool isDevelopment,
        out string origin)
    {
        var configured = configuration["OAuth:PublicOrigin"]
            ?? configuration["OAUTH_PUBLIC_ORIGIN"];
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri)
            && string.IsNullOrEmpty(configuredUri.UserInfo)
            && string.IsNullOrEmpty(configuredUri.Fragment)
            && string.IsNullOrEmpty(configuredUri.Query))
        {
            var isHttps = configuredUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            var isLocalDevelopmentHttp = isDevelopment
                && configuredUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && configuredUri.IsLoopback;
            if (isHttps || isLocalDevelopmentHttp)
            {
                origin = configuredUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
                return true;
            }
        }

        var host = context.Request.Host.Host;
        var localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configured)
            && isDevelopment
            && localHost
            && context.Request.Host.Port is not null
            && (context.Request.Scheme is "http" or "https"))
        {
            origin = $"{context.Request.Scheme}://{context.Request.Host}";
            return true;
        }

        origin = string.Empty;
        return false;
    }
}

public sealed record ChatGptStartRequest(string Code);
public sealed record ChatGptCompleteRequest(string State);
public sealed record OAuthRecoveryRequest(string State);