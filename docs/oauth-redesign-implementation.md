# OAuth Redesign — Implementation Report (redirect + callback, Gemini/OpenAI/Grok)

**Date:** 2026-07-20
**Branch:** `feat/oauth-redirect` → merged/pushed to `main` (fast-forward, no regression)
**Status:** ✅ Implemented, built (0w/0e), tested (full suite green), deployed to gateway-host, live endpoints verified (HTTP 200).
**Model:** Server-owned Authorization-Code + PKCE flow. Employees connect with one click and never enter a secret; client id/secret are platform-held (seeded from env).

---

## 1. Why the previous build was replaced

The earlier OAuth build ("gateway-managed OAuth", device-code) was reported by the user as *"garbage, can't use it"*. Root defects:

| # | Defect | Effect |
|---|--------|--------|
| 1 | `redirect_uri` was set to the **authorize** URL, not a real callback path | Provider rejects; flow never completes |
| 2 | Callback only stashed `code` in an in-memory map; client had to *poll* to exchange | No real browser round-trip; device-code-shaped even for auth-code |
| 3 | Seeded OpenRouter/Anthropic/GitHub (device-code) | Not the requested providers |
| 4 | PKCE verifier held only in memory | Flow breaks on restart mid-flow |
| 5 | `State` validated loosely, not DB/tenant-bound | Weak CSRF; not restart-safe |

The model was wrong, not just buggy. A **server-owned redirect + callback** is the fix.

---

## 2. Target architecture

```
Dashboard (/providers)
  ├─ API-key providers  ............ existing rows (sealed key field)
  └─ OAuth providers (Gemini/OpenAI/Grok)
        [Connect] ──POST /oauth/{code}/start──▶ gateway builds
                    PKCE + state, returns { authorizationUrl }
        browser ──redirect──▶ accounts.google.com / auth.openai.com / api.x.com
        user logs in + approves
        provider ──redirect──▶ {host}/oauth/{code}/callback?code=..&state=..
        gateway ──POST token endpoint (server-side, code_verifier)──▶ token
        gateway stores sealed token (ProviderOAuthToken) + flips provider to OAuth
        browser tab shows "Connected — you may close this tab"
Subsequent /v1/* for that provider:
        OpenAiCompatChatServiceBase resolves live bearer via IOAuthTokenResolver
        → injected as Authorization: Bearer (already implemented, unchanged)
```

**Distinct from API keys:** the credential is a *user-consented OAuth token* brokered by the gateway, never an entered secret. Same `/providers` page, different "Auth" badge + Connect/Disconnect action.

---

## 3. What was built (file-by-file)

| File | Change |
|------|--------|
| `src/Arkana.Domain/Entities/OAuthPendingFlow.cs` | **New** — in-flight exchange state (TenantId, ProviderCode, State, sealed CodeVerifier, RedirectUri, expiry). Restart-safe. |
| `GatewayDbContext.cs` | DbSet `OAuthPendingFlows` + EF config (Tenant FK cascade, unique `State` index). |
| `IOAuthFlowService.cs` | Interface rewritten to providerCode model: `StartAsync(code, redirectBase)`, `HandleCallbackAsync(code, code, state)`, `GetStatusAsync(code)`, `DisconnectAsync(code)`, `GetValidAccessTokenAsync(Guid)` (connector side, unchanged). Records moved to global namespace. |
| `OAuthFlowService.cs` | **Rewrite.** Real authorize URL (PKCE S256 + state + redirect_uri from request host). `HandleCallbackAsync` validates state, exchanges server-side, seals tokens, flips provider to OAuth. `TryRefreshAsync` for token refresh. Uses `IAiProviderRepository` (not catalog) for write. |
| `OAuthEndpoints.cs` | **Rewrite.** `start` / `callback` (HTML echo, `AllowAnonymous`) / `disconnect` / `configs`, all by `providerCode`. No `9router` references. |
| `OAuthConfigSeeder.cs` | Seeds **Gemini** (public PKCE, no secret) / **OpenAI** / **Grok** (confidential, env secret) from `OAUTH_*_CLIENT_ID[/SECRET]`. Idempotent. |
| `Providers.razor` | Same-page **Connect** (no secret field), opens authorize URL, auto-polls status to Connected; Disconnect. |
| `Program.cs` | Added `/oauth` to the `ApiKeyAuthMiddleware` denylist so the dashboard cookie (not API key) gates it — mirrors `/providers`. |
| `IAiProviderRepository.cs` / `AiProviderRepository.cs` | Added `GetByCodeAsync`. |
| `IOAuthRepositories.cs` / `OAuthRepositories.cs` | Added `IOAuthPendingFlowRepository` + impl; fixed `GetByCodeAsync` to be EF-translatable + culture-safe. |
| `oauth-schema.sql` | Idempotent DDL incl. `OAuthPendingFlows`. |
| `tests/.../OAuthFlowServiceTests.cs` | Rewritten to redirect model (stub token endpoint; asserts real auth URL, PKCE posted, sealed storage, provider flip, refresh, bad-state rejection). |
| `docs/oauth-redesign-plan.md` | Status → IMPLEMENTED; resolved decisions; remaining infra actions. |
| `docs/oauth-integration.md` | Updated; no `9router` references. |

---

## 4. Issues found & fixed during this build (tracked)

1. **Compile — `_catalog` after rename.** Switched `OAuthFlowService` from `IProviderCatalog` to `IAiProviderRepository` but two `UpdateAsync` call sites still referenced `_catalog`. Fixed → `_providers`.
2. **Compile — `OAuthPendingFlow.Tenant` nav.** EF config referenced `x.Tenant` but the entity lacked the nav property. Added `public Tenant Tenant { get; private set; } = null!;`.
3. **Compile — `ClearExpiredPendingAsync` CA1822.** Made it `static` (no instance data); later simplified to parameterless.
4. **Compile — raw-string braces (CS9006).** `OAuthEndpoints.CallbackHtml` used a raw interpolated string with CSS `{{ }}`; collided with interpolation. Rewrote via `StringBuilder`.
5. **Compile — `OAuthFlowStartResult` rename.** Renamed to `OAuthStartResult`; updated `DashboardService` + `Providers.razor` references.
6. **Compile — `OAuthTokenStatus` not in scope.** Added `using Arkana.Domain.Entities;` to `OAuthEndpoints.cs`.
7. **Runtime — `/oauth` 401 "API key required".** The global `ApiKeyAuthMiddleware` wrapped `/oauth/*` (not in the denylist). Added `/oauth` to the `UseWhen` denylist in `Program.cs` so the dashboard cookie auth applies (like `/providers`).
8. **Runtime — `/oauth` 400/500 EF translation.** `string.Equals(..., OrdinalIgnoreCase)` inside a LINQ-to-Entities predicate is **not translatable** by EF Core. Fixed by materializing the tiny table then comparing in memory (`OrdinalIgnoreCase`) — also satisfies CA1304/CA1311/CA1862 under `TreatWarningsAsErrors`.

All fixed; Release build 0w/0e.

---

## 5. Verification gate (E2E)

| # | Check | Result |
|---|-------|--------|
| 1 | Release build + `dotnet publish` (linux-x64) | ✅ 0w/0e |
| 2 | Full test suite | ✅ Domain 191 · App 64 · Infra 343 · GatewayApi 206 |
| 3 | `GET /oauth/configs` (live) | ✅ 200, lists gemini/openai/grok |
| 4 | `POST /oauth/{code}/start` (live) | ✅ 200, real authorize URL w/ `redirect_uri`, `code_challenge`, `state`, `scope` |
| 5 | Restart-safe pending flow (DB-backed) | ✅ by design (unit-tested) |
| 6 | CSRF: bad `state` rejected | ✅ unit-tested |
| — | **Real browser round-trip → live token injection** | ⏳ BLOCKED on external: provider client id/secret + redirect-URI registration (see §6) |

---

## 6. Remaining external actions (NOT code)

1. **Provider credentials.** `client_id` is empty in the live deploy because `OAUTH_GEMINI_CLIENT_ID` / `OAUTH_OPENAI_CLIENT_ID`+`OAUTH_OPENAI_CLIENT_SECRET` / `OAUTH_GROK_CLIENT_ID`+`OAUTH_GROK_CLIENT_SECRET` are not set in `~/arkana-deploy/.env`. Add them and restart the gateway.
2. **Redirect-URI registration.** Register `{GATEWAY_BASE}/oauth/{gemini|openai|grok}/callback` in the Google / OpenAI / xAI developer consoles. The `redirect_uri` is derived from the request host at flow-start, so it must match what users actually hit (HTTP LAN now; HTTPS after the leaf-cert task for `https://gateway.arkana.dev`).
3. **Leaf-cert task** (separate, dormant): retire the local Codex adapter by issuing a TLS leaf cert for `gateway.arkana.dev` (Let's Encrypt DNS-01 / Cloudflare). Until then, remote Codex still depends on the Windows `:8892` adapter.

---

## 7. Deploy notes (gateway-host)

- Build image **directly** (`docker build -f ~/AI/Gateway/Dockerfile -t arkana-dev-gateway:latest ~/AI/Gateway`) — the compose `build.context: ..` resolves to `$HOME`, not the repo, so `docker compose up` would rebuild stale code.
- Apply schema additively: `OAuthPendingFlows` added via a minimal `CREATE TABLE IF NOT EXISTS` (the full `oauth-schema.sql` DROP block aborts on the existing `AiProviders→OAuthProviderConfigs` FK — only the new table is needed, the other two already exist from the prior deploy).
- Push pattern: build from a clean checkout of the published branch, ship the source archive to the host, rebuild and recreate only the gateway service.

*No `9router` references remain in code or OAuth docs. Historical environment mentions in `incident-2026-07-20-codex-streaming-regression.md` are left as factual context.*
