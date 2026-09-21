# Plan — OAuth redesign: server-orchestrated redirect + callback

**Status:** IMPLEMENTED (build + unit/integration tests green). Replaces the broken device-code/paste
OAuth build. Approved model: **server-owned redirect + callback** — the gateway server owns the full
Authorization-Code + PKCE flow, including the browser callback; tokens brokered
server-side into the request pipeline (distinct from API keys, but surfaced on the
same `/providers` page).

**Targets:** Gemini, OpenAI, Grok (xAI). Same page also keeps API-key providers.
**Employees enter ZERO secrets** — client id/secret are platform-held (seeded from
env), matching the platform-held credential pattern: the gateway owns the app
client id/secret server-side.

**Resolved decisions (from §10):**
- **Gateway public base URL:** the callback redirects to `{Scheme}://{Host}` seen by
  the browser at flow-start time (request-derived), so it works on both
  `http://gateway.arkana.dev:80` now and `https://gateway.arkana.dev` after the
  leaf cert. The *registered* redirect URI in each provider console must match what
  users actually hit (ties to leaf-cert task).
- **Client secrets:** read from env (`OAUTH_OPENAI_CLIENT_SECRET` / `OAUTH_GROK_CLIENT_SECRET`);
  Gemini is public (PKCE, no secret). Supplied via `~/arkana-deploy/.env` on gateway-host.
- **Strictly Gemini/OpenAI/Grok** (dropped OpenRouter/Anthropic/GitHub).
- **Tenant:** single default tenant (existing pattern).

**Remaining infra action (external, not code):** pre-register
`{GATEWAY_BASE}/oauth/{gemini|openai|grok}/callback` in the Google / OpenAI / xAI
developer consoles; supply the OpenAI/Grok client id+secret in `.env`. Then run the
E2E verification gate (§9) against a real browser round-trip.

---

## 1. Why the current build is "garbage" (what we replace)

| # | Defect in current `OAuthFlowService`/`OAuthEndpoints` | Effect |
|---|------------------------------------------------------|--------|
| 1 | `BuildAuthUrl` sets `redirect_uri = cfg.AuthorizationEndpoint` (the *authorize* URL, not a real callback path) | Provider rejects / callback never lands → flow never completes |
| 2 | `/oauth/callback` only stashes `code` in an in-memory map; client must *poll* to exchange | No real browser redirect round-trip; device-code-shaped even for auth-code |
| 3 | Seeded templates were OpenRouter/Anthropic/GitHub (device-code) | Not the providers you want (Gemini/OpenAI/Grok) |
| 4 | PKCE verifier held only in memory (lost on restart mid-flow) | Flow breaks on restart |
| 5 | `State` validated loosely; no CSRF binding to tenant/session | Weak; also not tied to dashboard session |

The model is wrong, not just buggy. A server-owned callback is the fix.

---

## 2. Target architecture (server-owned redirect + callback)

```
Dashboard (/providers)
  ├─ API-key providers  ............ existing rows (API key field, sealed)
  └─ OAuth providers (Gemini/OpenAI/Grok)
        [Connect] ──GET /oauth/{provider}/authorize──▶ gateway builds
                    PKCE + state, returns { authUrl }
        browser ──redirect──▶ accounts.google.com / openai.com / x.com
        user logs in
        provider ──redirect──▶ {GATEWAY_BASE}/oauth/{provider}/callback?code=..&state=..
        gateway ──POST token endpoint (server-side, with code_verifier)──▶ token
        gateway stores sealed token (ProviderOAuthToken) bound to provider+tenant
        browser shown "Connected — you may close this tab"
Subsequent /v1/* requests for that provider:
        OpenAiCompatChatServiceBase resolves live bearer via IOAuthTokenResolver
        (already implemented) → injected as Authorization: Bearer
```

**Different from API keys:** the credential is a *user-consented OAuth token* brokered
by the gateway, never an entered secret. Same page, different "Auth" badge + a
**Connect / Reconnect / Disconnect** action instead of a key field.

---

## 3. Server endpoints (redirect + callback)

```
GET  /oauth/{providerCode}/authorize
        → build PKCE (verifier+challenge) + state (CSRF, bound to tenant)
        → persist pending flow in DB (ProviderOAuthToken row, Pending) so it
          survives restart; verifier + state stored (sealed/short-lived)
        → return { authUrl, state }   (authUrl points at the REAL provider)
        authUrl = {cfg.AuthorizationEndpoint}?response_type=code
                  &client_id={cfg.ClientId}
                  &redirect_uri={GATEWAY_BASE}/oauth/{providerCode}/callback
                  &scope={cfg.Scopes}&state={state}&code_challenge={challenge}
                  &code_challenge_method=S256
        NOTE: redirect_uri MUST equal the provider's pre-registered one.

GET  /oauth/{providerCode}/callback?code=..&state=..
        → look up pending flow by state; verify state matches (CSRF)
        → POST {cfg.TokenEndpoint} with grant_type=authorization_code,
          code, code_verifier, redirect_uri, client_id (+client_secret if conf.)
        → on 200: seal access + refresh tokens into ProviderOAuthToken (Connected)
        → clear pending; return HTML "Connected — close tab" (or redirect to /providers)
        → AllowAnonymous (user-agent hits it). State check is the security control.

POST /oauth/{providerCode}/disconnect   (dashboard auth)
        → revoke token row for that provider+tenant

GET  /oauth/configs   (dashboard auth)  → list connectable OAuth providers + status
```

Provider identity is by **`providerCode`** (gemini/openai/grok) — not the internal
`AiProvider` GUID — so the dashboard can offer "Connect Gemini" before any
`AiProvider` row exists, then bind the stored token to the matching provider row.

### Pending-flow storage (fixes defect #4)
New table/row: `OAuthPendingFlow (Id, TenantId, ProviderCode, State, CodeVerifier,
ClientId, RedirectUri, ExpiresAt, CreatedAt)`. Replaces the in-memory `_pending`
dict. Tokens are exchanged server-side in the callback, so nothing secret lives in
memory unnecessarily.

---

## 4. Seed data — Gemini, OpenAI, Grok (replaces old templates)

New `OAuthProviderConfig` rows (idempotent seeder). Confidential clients need
`ClientSecret` from env (`OAUTH_*_CLIENT_SECRET`); public clients (Gemini PKCE-only)
omit it. `AuthorizationEndpoint`/`TokenEndpoint` are the **providers' real URLs**.

| Provider | Auth endpoint | Token endpoint | Scopes | Client type | Env for secret |
|---|---|---|---|---|---|
| **Gemini** (Google) | `https://accounts.google.com/o/oauth2/v2/auth` | `https://oauth2.googleapis.com/token` | `https://www.googleapis.com/auth/generative-language` (or `openid email`) | Public (PKCE, no secret) | — |
| **OpenAI** | `https://auth.openai.com/authorize` | `https://auth.openai.com/oauth/token` (verify exact) | `model.request` (+ offline_access for refresh) | Confidential (secret) | `OAUTH_OPENAI_CLIENT_SECRET` |
| **Grok** (xAI) | `https://accounts.x.com/oauth2/authorize` (or x.com/authorize) | `https://api.x.com/2/oauth2/token` | `model.request` (+ offline_access) | Confidential (secret) | `OAUTH_GROK_CLIENT_SECRET` |

> Exact OpenAI/Grok authorize+token URLs and required scopes must be confirmed
> against current provider docs before deploy (xAI recently consolidated to
> `api.x.com/2/oauth2/token`). The seeder reads client id/secret from **env**
> (`OAUTH_GEMINI_CLIENT_ID`, `OAUTH_OPENAI_CLIENT_ID/SECRET`, `OAUTH_GROK_CLIENT_ID/SECRET`),
> never hardcoded (SOP). Empty id allowed (template still registers; Connect shows
> "configure client id").

Redirect URI each provider must be pre-registered with:
`{GATEWAY_BASE}/oauth/{gemini|openai|grok}/callback`.
→ **Infra action:** register these redirect URIs in the Google / OpenAI / xAI
developer consoles (the gateway's public base URL is needed — ties to the leaf-cert
task: `https://gateway.arkana.dev/oauth/.../callback`).

---

## 5. Request pipeline (token injection — already built, keep)

`OpenAiCompatChatServiceBase` already calls `IOAuthTokenResolver.GetBearerTokenAsync`
when `provider.UsesOAuth`. Keep it. The resolver returns the live (auto-refreshed)
sealed bearer. **No change needed here** — the fix is making the *connect* flow
actually complete so a token exists to resolve.

Auto-refresh (`OAuthTokenRefreshService`, already built) stays; it refreshes by
`refresh_token` against `cfg.TokenEndpoint`. Good.

---

## 6. Dashboard UI (`Providers.razor`) — same page, two auth kinds

- Each provider row shows an **Auth badge**: `API` or `OAuth`.
- API-key rows: existing key field + show/hide.
- OAuth rows (Gemini/OpenAI/Grok): a **Connect** button → calls `/oauth/{code}/authorize`,
  opens `authUrl` in a new tab; status polls `/oauth/configs` (Connected/Pending/Error).
  On callback the tab shows "Connected" and the row flips to green. **Reconnect** /
  **Disconnect** actions.
- A small "OAuth connections" section listing the 3 providers + their status,
  independent of the API-key table, but visually on the same page.
- No modal-with-pasted-code (that was the device-code smell). Just "Connect → login
  in browser → done".

---

## 7. Entities / migrations

- New: `OAuthPendingFlow` entity + `DbSet` + EF config (TenantId FK, expires index).
- `OAuthProviderConfig`: add `ProviderCode` is already the key; ensure
  `AuthorizationEndpoint`/`TokenEndpoint`/`Scopes`/`ClientId`/`SealedClientSecret`/
  `IsConfidential` columns exist (they do from prior build).
- `ProviderOAuthToken`: already exists; reuse for the stored connected token.
- DB: live uses `EnsureCreatedAsync` (no EF migrations) → generate fresh
  `oauth-schema.sql` including the new `OAuthPendingFlow` table and apply to
  `arkana-postgres` (same pattern as before).

---

## 8. Files to change (concrete)

- `src/Arkana.Domain/Entities/OAuthPendingFlow.cs` (new)
- `src/Arkana.Domain/Entities/OAuthProviderConfig.cs` — add `IsConfidential` if missing
- `src/Arkana.Infrastructure/Persistence/GatewayDbContext.cs` — DbSet + config + new table
- `src/Arkana.Infrastructure/OAuth/OAuthFlowService.cs` — **rewrite** the auth-code
  path: real `redirect_uri`, DB-backed pending flow, server-side exchange in callback.
  Keep device-code path only if a provider needs it (none of Gemini/OpenAI/Grok do).
- `src/Arkana.Infrastructure/OAuth/IOAuthFlowService.cs` — adjust signatures
  (use `providerCode`, add callback exchange method)
- `src/Arkana.Gateway.Api/Endpoints/OAuthEndpoints.cs` — **rewrite**: `authorize`
  (by providerCode), `callback` (server-side token exchange → HTML), `disconnect`,
  `configs`. Protect with dashboard auth appropriately (`callback` AllowAnonymous +
  state check).
- `src/Arkana.Gateway.Api/Services/OAuthConfigSeeder.cs` — seed **Gemini/OpenAI/Grok**
  from env; drop the old OpenRouter/Anthropic/GitHub device-code templates (or keep
  OpenRouter as a bonus, but make the 3 requested the primary).
- `src/Arkana.Gateway.Api/Components/Pages/Providers.razor` — OAuth Connect UI
  (same page).
- `oauth-schema.sql` — regenerate with `OAuthPendingFlow`.
- `docs/oauth-integration.md` — update to the redirect model.

---

## 9. Verification gate (E2E, must pass before "done")

1. `GET /oauth/gemini/authorize` returns a real `authUrl` whose `redirect_uri`
   equals `{GATEWAY_BASE}/oauth/gemini/callback`.
2. Full browser round-trip (real or a recorded replay): authorize → callback →
   `ProviderOAuthToken` row = **Connected** with sealed tokens.
3. A `/v1/responses` (or `/v1/chat/completions`) call for the Gemini/OpenAI/Grok
   provider injects `Authorization: Bearer <live token>` and the upstream accepts it
   (200, real model output). **This is the real proof** — not a synthetic probe.
4. Restart the gateway mid-flow: pending flow survives (DB-backed), callback still
   exchanges.
5. CSRF: callback with mismatched `state` → rejected (no token stored).
6. Unit/integration test: `OAuthFlowServiceTests` updated to the redirect model
   (stub token endpoint, assert sealed storage + refresh).

---

## 10. Open decisions for the user (before build)

- [ ] **Gateway public base URL** for `redirect_uri`: is it
      `https://gateway.arkana.dev` (needs the leaf cert from the pending task) or
      the current `http://gateway.arkana.dev:80` / LAN IP? Provider consoles need a
      *registered* redirect URI — must be stable. **This links to the leaf-cert task.**
- [ ] **OpenAI/Grok exact authorize+token URLs + scopes** — confirm against current
      docs (I'll verify during build, but flag if you already know them).
- [ ] **Client secrets source**: env vars in `~/arkana-deploy/.env` on gateway-host
      (current pattern) — confirm you'll supply `OAUTH_OPENAI_CLIENT_SECRET` /
      `OAUTH_GROK_CLIENT_SECRET`. Gemini is public (no secret).
- [ ] **Tenant model**: tokens are tenant-scoped. For now single default tenant
      (existing pattern) — OK?
- [ ] Keep **OpenRouter** as a 4th OAuth template, or strictly Gemini/OpenAI/Grok?

---

## 11. Build order (when approved — NOT now)

1. Add `OAuthPendingFlow` entity + context + regenerate `oauth-schema.sql`; apply to PG.
2. Rewrite `OAuthFlowService` auth-code path (real redirect_uri, DB pending, server exchange).
3. Rewrite `OAuthEndpoints` (authorize/callback/disconnect/configs) + interface.
4. Reseed `OAuthConfigSeeder` for Gemini/OpenAI/Grok (env-driven).
5. Update `Providers.razor` Connect UI (same page).
6. Build + run full test suite (update `OAuthFlowServiceTests`).
7. Deploy to gateway-host; register redirect URIs in provider consoles (infra).
8. E2E verification gate (§9) — real browser round-trip + live token injection.

---

*Planning document — no code written. Blocks on: (a) user approval, (b) the gateway
public base URL decision (ties to leaf-cert task), (c) client secrets for OpenAI/Grok.*
