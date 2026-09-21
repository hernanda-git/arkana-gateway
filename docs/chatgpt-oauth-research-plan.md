# ChatGPT OAuth 2.0 Integration — Research & Execution Plan

**Phase:** 1 (Research + Planning ONLY). No production changes made.
**Gateway:** `https://gateway.arkana.dev` — ARKANA GATEWAY (.NET 10 Blazor Server)
**Date:** 2026-08-20
**Researchers:** Hermes Agent (autonomous)
**Repo investigated (gateway):** `C:/Workspace/arkana-gateway` @ `c3cc470` (main)
**Reference repos:**
- OpenCode `github.com/anomalyco/opencode` @ `e2505d4` (2026-08-19)
- Hermes Agent `github.com/NousResearch/hermes-agent` @ `ce9d48c` (2026-08-19)

---

## 1. Executive Summary

**Current state.** The ARKANA GATEWAY already implements a **server-orchestrated OAuth 2.0
Authorization-Code + PKCE** framework (`OAuthFlowService`, `OAuthEndpoints`, `OAuthTokenResolver`,
sealed `ProviderOAuthToken` rows, envelope-encrypted vault). It is live and working for Gemini/OpenAI/Grok
*OAuth configs* and already has the **per-provider-code multi-account scaffolding** (`gemini-acc1`,
`gemini-acc2` style codes, `Label` on tokens, `OAuthConfigId` on `AiProvider`). What it does **NOT**
yet have is a **ChatGPT/Codex subscription OAuth config** wired to OpenAI's auth server, plus the
account-pool/routing layer that turns a list of authenticated accounts into a load-balanced upstream
selector.

**Desired state.** An admin UI at `gateway.arkana.dev` where an operator registers N ChatGPT
subscription accounts, authenticates each through OpenAI's **Codex OAuth** (the same flow OpenCode and
Hermes use), stores the resulting tokens sealed in the existing vault, sees per-account status/health,
and the gateway routes `/v1` traffic across those accounts by priority/health/round-robin.

**Feasibility: TECHNICALLY FEASIBLE — but with hard caveats.**

The mechanism is real and used in production by OpenCode and Hermes today. It is **OpenAI's official
Codex OAuth** against `https://auth.openai.com`, using a **public client** (`client_id =
app_EMoamEEZ73f0CkXaXp7hrann`) that OpenAI ships inside its own `codex` CLI. The access token
authenticates against `https://chatgpt.com/backend-api/codex` (and `/responses`), and the per-account
identity is sent as the `ChatGPT-Account-Id` header (derived from the JWT `chatgpt_account_id` claim).

**Major constraints (must be communicated to the owner before implementation):**

1. **It is a subscription entitlement, not an API-key product.** The token grants the *ChatGPT
   subscription's* Codex quota on `chatgpt.com/backend-api/codex` — models are the Codex/subscription
   family (gpt-5.x-codex, etc.), NOT the standard `api.openai.com/v1` catalog. If the gateway expects
   `gpt-4o`/`o1` from this account, it will fail. An **adapter / model-remap** is required.
2. **`auth.openai.com` has NO public OAuth registration portal for this client.** Both OpenCode and
   Hermes hardcode the *same* `client_id` — it is OpenAI's own first-party Codex client. We would be
   **reusing OpenAI's first-party client_id**. This works (it's what the reference tools do) but it is
   not a client *we* registered, and its validity depends on OpenAI not revoking/rotating that
   client_id or tightening the allowed `redirect_uri` (today it accepts `http://localhost:1455` and
   `http://localhost:43827`). **This is the single biggest stability/ToS risk** and must be flagged.
3. **Device-authorization flow requires interactive user code entry** (the OpenCode "headless" method),
   or a localhost callback that the *server* must host (the OpenCode "browser" method binds
   `localhost:1455`). On a headless gateway server behind a VPN, the **browser/localhost callback must
   be proxied or the device-code method used**, and the gateway must capture the code.
4. **One refresh token, single-use.** Codex OAuth refresh tokens are single-use; sharing one token set
   across the gateway and a user's Codex CLI causes `refresh_token_reused` failures. The gateway must
   own the token exclusively.
5. **Quota is per ChatGPT account and is opaque** (HTTP 429 = usage/quota, not auth). Account rotation
   must treat 429 as "try another account / cooldown," not "re-auth."

**Recommended approach.** Extend the *existing* OAuth framework (do not rebuild it) with a new
`OAuthProviderConfig` for ChatGPT/Codex (`codex` grant), add a **server-hosted device-code + callback
capture** path (since the gateway is server-side, not a desktop app), persist tokens in the existing
`ProviderOAuthToken` table (one row per `chatgpt-accN` provider), build the `ChatGPT-Account-Id`
header injection + a `/v1` model remap to the Codex catalog, and add an **account pool + health-aware
router** that selects among the authenticated `chatgpt-acc*` providers.

---

## 2. Current Gateway Architecture

```
Client / Agent (OpenCode, Codex, any OpenAI-compatible)
   │  X-Api-Key / Authorization: Bearer <gateway key>
   ▼
gateway.arkana.dev  (apache2 :443 → Kestrel :5011)
   │  ApiKeyAuthMiddleware (X-Api-Key / Bearer)  → /v1, /mcp
   │  AdminAuthMiddleware (ADMIN_API_KEY / cookie) → /admin
   │  RoleAuthorizationMiddleware → dashboard pages
   ▼
Arkana.Gateway.Api  (.NET 10 Minimal API + Blazor Server UI)
   │  MediatR handlers (SendChatHandler etc.)
   ▼
Provider Connectors (OpenAiCompatChatServiceBase subclasses)
   │  resolve credential: OAuthTokenResolver → ProviderOAuthToken (sealed bearer)
   │                     OR AiProvider.DecryptApiKey (sealed API key)
   ▼
Upstream providers:
   - opencode  → https://opencode.ai/zen/go/v1   (API key: OPENCODE_GO_API_KEY)
   - gemini    → OAuth (Google)  → generativelanguage.googleapis.com
   - openai    → OAuth config exists (seeded) — pending a real OpenAI OAuth client
   - grok      → OAuth (xAI)
   - ollama    → local (no auth)
   - others    → Groq/OpenRouter/Qwen/GLM/Cloudflare (API key)

Postgres (arkana) · Redis · Qdrant · n8n   (docker compose, gateway-host VPS)
```

**Key facts established by inspecting the live repo + deploy skill:**

- Stack: .NET 10, Blazor Server dashboard, Minimal API for `/v1` & `/oauth` & `/admin`.
- Credentials are **envelope-encrypted at rest** (`EnvelopeCredentialVault`, AES-256-GCM, per-secret
  DEK wrapped by a master key from `ARKANA_MASTER_KEY` / file / auto-gen). Both `AiProvider.ApiKey`
  and `ProviderOAuthToken` access/refresh tokens are sealed — never plaintext.
- OAuth is **server-orchestrated**: the gateway builds the authorize URL (PKCE + state), the browser
  redirects to the provider, the provider redirects back to `gateway/oauth/{code}/callback`, and the
  **gateway** exchanges the code for tokens server-side. Client id/secret are platform-held (seeded from
  env via `OAuthConfigSeeder`). Employees never paste a secret.
- PKCE (`S256`), cryptographically random `state` (CSRF), `OAuthPendingFlow` row persisted so a
  restart mid-flow does not lose the verifier, exact `redirect_uri` matching, 10-minute flow TTL.
- Multi-account scaffolding already exists: `AiProvider.OAuthConfigId`, `OAuthPendingFlow.AiProviderCode`
  (`gemini-acc1`), `ProviderOAuthToken.Label`, and `OAuthFlowService.StartAsync` resolves the target
  provider by code.
- Admin API is protected by `ADMIN_API_KEY` (fails closed). Dashboard pages by cookie/role middleware.
- The connector layer already supports OAuth: `OpenAiCompatChatServiceBase.CompleteAsync` calls
  `_oauthResolver.GetBearerTokenAsync(provider.Id)` when `provider.UsesOAuth`, else falls back to the
  sealed API key. So an OAuth-backed provider *already* sends `Authorization: Bearer <token>`.

**What is missing for ChatGPT specifically:**
1. An `OAuthProviderConfig` for the ChatGPT/Codex subscription (auth server `auth.openai.com`, token
   endpoint, client_id, grant type).
2. Support for OpenAI's **Codex OAuth specifics**: device-code grant *and/or* a server-side callback
   capture (the gateway is a server, not a desktop with a localhost browser), plus sending the
   `ChatGPT-Account-Id` header.
3. A model/request **adapter** from the gateway's `/v1` catalog to the Codex `/backend-api/codex`
   surface and its subscription model names.
4. The **account pool + health-aware router** that selects among `chatgpt-acc*` providers.

---

## 3. OpenCode Authentication Analysis

**Commit investigated:** `e2505d434a6d78904ecfe546c4a1980d26bd8cd1` (2026-08-19). TypeScript/Bun monorepo
(note: the brief called OpenCode "Go" — it is in fact a TS/Bun monorepo; the CLI is Bun-compiled).

**Primary file:** `packages/core/src/plugin/provider/openai.ts` (v2 / effect plugin). Also
`packages/opencode/src/provider/auth.ts` (generic OAuth framework) and
`packages/core/src/plugin/provider/opencode.ts` (the OpenCode *platform* device flow, distinct).

**ChatGPT subscription auth — two methods, same `client_id`:**

| Field | Value |
|---|---|
| `client_id` | `app_EMoamEEZ73f0CkXaXp7hrann` (OpenAI's first-party Codex client, identical in Hermes) |
| issuer | `https://auth.openai.com` |
| authorize | `https://auth.openai.com/oauth/authorize` |
| token | `https://auth.openai.com/oauth/token` |
| device code (headless) | `https://auth.openai.com/api/accounts/deviceauth/usercode` + `/token` |
| callback (browser) | `http://localhost:1455/auth/callback` |
| scope | `openid profile email offline_access` (+ `id_token_add_organizations=true`, `codex_cli_simplified_flow=true`, `originator=opencode`) |
| PKCE | `S256`, 43-char verifier, random 32-byte `state` (CSRF) |
| grant types | `authorization_code` (browser), `refresh_token`, device-authorization (headless) |

**Flow specifics:**
- **Browser method:** spins a `localhost:1455` HTTP server, generates PKCE + state, opens the provider
  URL, the provider redirects back with `?code&state`, validates `state`, then `POST /oauth/token`
  (`grant_type=authorization_code`, `code_verifier`). Returns `access_token`, `refresh_token`,
  `id_token`, `expires_in`.
- **Headless method:** hits `/api/accounts/deviceauth/usercode` (`client_id`) → gets `user_code` +
  `device_auth_id`; user visits `https://auth.openai.com/codex/device` and enters the code; OpenCode
  polls `/api/accounts/deviceauth/token` until it returns `authorization_code` + `code_verifier`; then
  exchanges via `/oauth/token`.
- **Refresh:** `POST /oauth/token` with `grant_type=refresh_token`, `refresh_token`, `client_id`.
- **Account identity:** the JWT `id_token` / `access_token` carries `chatgpt_account_id` (and/or
  `https://api.openai.com/auth.chatgpt_account_id`). OpenCode extracts it into credential `metadata.accountID`.
- **No cookies / no `chatgpt.com/backend-api` session cookies** are used by OpenCode itself — it uses the
  OAuth token against the Codex API surface. (Hermes, below, is the one that targets
  `chatgpt.com/backend-api/codex` directly and reads the same claim.)

**Persistence:** OpenCode stores the OAuth credential via its `Credential.OAuth` model (in the local
instance state / keychain). Not a server DB — it's a desktop/local agent.

**Multi-account:** the framework supports multiple providers/methods but the ChatGPT plugin itself is a
single integration (`integrationID "openai"`); multiple ChatGPT *accounts* would each be a separate
credential entry the user switches between — not a built-in pool.

**Classification of the mechanism:** **(b) a real OAuth 2.0 Authorization-Code + PKCE flow against
OpenAI's auth server** (`auth.openai.com`), using OpenAI's **first-party public client_id**. It is the
*official* Codex CLI OAuth — NOT an undocumented `chatgpt.com` cookie scrape. The token is then used
against OpenAI's Codex API (`chatgpt.com/backend-api/codex`), which is OpenAI's documented Codex
subscription endpoint. **Caveat:** reusing `app_EMoamEEZ73f0CkXaXp7hrann` is relying on a client_id we
did not register; its continued acceptance of arbitrary localhost redirect URIs is a stability/ToS risk.

---

## 4. Hermes Agent Authentication Analysis

**Commit investigated:** `ce9d48ce85ce9d7d20ded8c406bea8ef89aa14b3` (2026-08-19). Python + TypeScript.

**Primary files:**
- `hermes_cli/auth.py` — OAuth constants + Codex token refresh (`refresh_codex_oauth_pure`).
- `agent/credential_pool.py` — seeds `openai-codex` pool entries from local auth state;
  `base_url = "https://chatgpt.com/backend-api/codex"`.
- `agent/auxiliary_client.py` (`_extract_codex_headers`) and `agent/model_metadata.py`
  (`_extract_chatgpt_account_id`) — read `chatgpt_account_id` claim → `ChatGPT-Account-Id` header.

**Constants (verified):**
```
DEFAULT_CODEX_BASE_URL = "https://chatgpt.com/backend-api/codex"
CODEX_OAUTH_CLIENT_ID = "app_EMoamEEZ73f0CkXaXp7hrann"   # SAME as OpenCode
CODEX_OAUTH_TOKEN_URL = "https://auth.openai.com/oauth/token"
CODEX_OAUTH_USER_AGENT = "hermes-cli/<version>"
```
Provider registry entry: `openai-codex` → `auth_type="oauth_external"`, `inference_base_url=DEFAULT_CODEX_BASE_URL`.
Separate `openai-api` → `auth_type="api_key"`, `https://api.openai.com/v1` (the *platform* API key path).
So **Hermes explicitly distinguishes ChatGPT-subscription (Codex OAuth) from OpenAI Platform API key.**

**Flow specifics:**
- Hermes obtains the Codex OAuth token (device-code via `hermes auth`, stored in the Hermes auth
  singleton `~/.hermes/.../openai-codex` state as `tokens{access_token, refresh_token}`).
- At request time it calls `chatgpt.com/backend-api/codex` (and `/responses`) with
  `Authorization: Bearer <access_token>` **and** `ChatGPT-Account-Id: <chatgpt_account_id>` (extracted
  from the JWT claim `https://api.openai.com/auth.chatgpt_account_id`). It also pins `User-Agent:
  codex_cli_rs/...` and `originator: codex_cli_rs` to match OpenAI's expected client shape.
- **Refresh:** `refresh_codex_oauth_pure` → `POST auth.openai.com/oauth/token`
  (`grant_type=refresh_token`, `refresh_token`, `client_id`). Handles `429` (quota, not auth →
  "retry later, credentials still valid"), `invalid_grant`/`refresh_token_reused` (force re-login),
  `401/403` (force relogin).
- **Multi-account:** the credential pool supports multiple *entries* per provider with a `label`
  (e.g. `label_from_token`). `credential_pool.py` has `manual:device_code` and `device_code` sources and
  rotates among healthy entries (cooldown on 429). It reads the `chatgpt_account_id` to label accounts.
  So Hermes *does* have a multi-account pool concept (label + rotate + cooldown) — a good reference for
  our router.

**Classification:** same as OpenCode — **(b) official Codex OAuth** against `auth.openai.com`, token
used against `chatgpt.com/backend-api/codex` (OpenAI's Codex subscription API). **Undocumented/internal?**
The Codex `/backend-api/codex` surface is not a broadly published REST reference like `api.openai.com/v1`,
but it is the endpoint OpenAI's own Codex product uses and the OAuth is OpenAI's own first-party flow.
It is **not** a scraped `chatgpt.com` web-session cookie — it is a proper OAuth bearer against a
subscription API. Still, it is **less "officially documented" than the Platform API**, and depends on
OpenAI keeping `app_EMoamEEZ73f0CkXaXp7hrann` usable.

---

## 5. OpenCode vs Hermes Comparison

| Aspect | OpenCode | Hermes Agent |
|---|---|---|
| ChatGPT mechanism | OAuth 2.0 Auth-Code + PKCE @ `auth.openai.com` | Same OAuth (Codex) @ `auth.openai.com` |
| client_id | `app_EMoamEEZ73f0CkXaXp7hrann` | `app_EMoamEEZ73f0CkXaXp7hrann` (identical) |
| Methods | browser (localhost:1455 callback) + headless device-code | device-code (`hermes auth`), singleton state |
| Inference endpoint | `@ai-sdk/openai` (Responses API) via OAuth bearer | `chatgpt.com/backend-api/codex` (+`/responses`) |
| Account header | `metadata.accountID` (chatgpt_account_id) | `ChatGPT-Account-Id: <claim>` header |
| Single-use refresh | yes (refresh_token rotation) | yes; detects `refresh_token_reused` |
| Multi-account | multiple credential entries, manual switch | **pool + rotate + cooldown** per label |
| Persists where | local instance state / keychain | `~/.hermes` auth singleton + credential pool |
| Distinct from Platform API key? | yes (`openai` api_key is separate) | yes (`openai-api` vs `openai-codex`) |

**Takeaways for our gateway:**
- Reuse the **exact same OAuth endpoints + client_id** (it's OpenAI's first-party Codex client).
- Mirror Hermes' **multi-account pool + cooldown + 429-as-quota** logic in our router.
- Mirror the **`ChatGPT-Account-Id` header** injection (claim → header) — this is required or the
  Codex endpoint returns an empty model list / wrong account context.
- Treat `refresh_token` as single-use and **own** it exclusively (no sharing with a user's Codex CLI).

---

## 6. Recommended Architecture

We do **not** rebuild OAuth. We extend the existing server-orchestrated framework.

```
Admin
  ▼  (Blazor dashboard "ChatGPT Accounts" page, Admin-auth required)
Gateway Management UI
  ▼  POST /admin/chatgpt/accounts            (create chatgpt-accN provider + OAuth config link)
  ▼  POST /oauth/chatgpt/start  (or device-code start)
OAuth Authorization Flow
  ├─ Browser method: gateway hosts the callback capture (see §7) → auth.openai.com
  └─ Headless method: device-code, gateway polls, captures code+verifier
  ▼  POST auth.openai.com/oauth/token  (gateway, server-side)
Encrypted Credential Store  (ProviderOAuthToken, sealed via EnvelopeCredentialVault, master key)
  ▼  one row per chatgpt-accN
Account Pool  (in-memory + DB: list of healthy, non-expired, enabled chatgpt-acc* providers)
  ▼  AccountPool.Selector  (health-aware, round-robin/least-recently-used, 429 cooldown)
Gateway Router  (SendChatHandler → picks provider → connector)
  ▼  OpenAiCompatChatServiceBase (OAuth) → adds ChatGPT-Account-Id header
ChatGPT-Account-Id header + Bearer → https://chatgpt.com/backend-api/codex[/responses]
```

**Components to add/extend:**
1. `OAuthProviderConfig` seed/row: `code = "chatgpt"`, `GrantType = CodexDevice` (or `AuthorizationCode`
   with server callback), `AuthorizationEndpoint = https://auth.openai.com/oauth/authorize`,
   `TokenEndpoint = https://auth.openai.com/oauth/token`, `ClientId = app_EMoamEEZ73f0CkXaXp7hrann`,
   `Scopes = "openid profile email offline_access"`, optional device endpoints.
2. A **server-side callback host** (the gateway already exposes `/oauth/{code}/callback`; we reuse it but
   must accept `http://localhost:1455` style only if the browser runs on the same machine — on a remote
   gateway we instead use the **device-code** method so no localhost bind is needed; the gateway captures
   the code by polling `auth.openai.com/api/accounts/deviceauth/token`).
3. `ChatGptCodexChatService` (subclass of `OpenAiCompatChatServiceBase`): sets `Endpoint =
   https://chatgpt.com/backend-api/codex` (or `/responses`), injects `ChatGPT-Account-Id` from the JWT
   claim, pins `User-Agent`/`originator` like Hermes.
4. **Adapter / model remap:** map gateway `/v1` model names to the Codex catalog (gpt-5.x-codex family);
   reject/remap non-Codex models with a clear error (mirror the gateway's existing pre-flight remap
   pattern for opencode).
5. `AccountPool` service: in-memory registry of `chatgpt-acc*` providers with health + cooldown +
   `ChatGPT-Account-Id`; selection strategy pluggable (round-robin / LRU / health-aware).
6. Admin + dashboard pages for account CRUD/status/enable-disable/health.

---

## 7. OAuth Flow (sequence)

```
Admin (browser)          Gateway                auth.openai.com          chatgpt.com/backend-api
   │  POST /oauth/chatgpt/start                                       │
   ├──────────────────────────────────▶│                              │
   │                                     │ builds PKCE+state,         │
   │                                     │ persists OAuthPendingFlow   │
   │◀── authorize URL ───────────────────┤                             │
   │  open URL (browser/device) ──────────────────────────────────────▶│  (user logs in)
   │                                     │  (device method: poll       │
   │                                     │   /deviceauth/token)        │
   │  provider redirects / polls ──────────────────────────────────────▶│ code (+verifier)
   │                                     │◀── code ────────────────────┤
   │                                     │ POST /oauth/token ──────────▶│
   │                                     │◀── access+refresh+id_token ──┤
   │                                     │ seal tokens → ProviderOAuthToken (chatgpt-accN)
   │                                     │ extract chatgpt_account_id → Label
   │  GET /oauth/chatgpt/status ────────▶│ status=Connected
   │                                     │                             │  (later, per request)
   │  POST /v1/chat/completions ────────▶│ AccountPool.Select → chatgpt-acc2
   │                                     │ Bearer + ChatGPT-Account-Id ▶│  inference
```

---

## 8. Database Design

**Reuse existing tables — no new tables strictly required:**

- `OAuthProviderConfigs` — add one row: `code="chatgpt"`, `GrantType=CodexDevice/AuthorizationCode`,
  `TokenEndpoint`, `ClientId`, `Scopes`.
- `AiProviders` — one row per account: `code="chatgpt-acc1"`, `AuthMethod=OAuth`,
  `OAuthConfigId=<chatgpt config id>`, `BaseUrl=https://chatgpt.com/backend-api/codex`, `Priority`,
  `IsEnabled`, `TenantId`.
- `ProviderOAuthTokens` — one row per `chatgpt-accN`: `SealedAccessToken`, `SealedRefreshToken`,
  `TokenType`, `ExpiresAt`, `RefreshExpiresAt`, `Status`, `LastError`, `Label` (= account id / display
  name), `TenantId`. Already has the right columns.
- `OAuthPendingFlows` — already supports `AiProviderCode` for multi-account binding.

**Optional new table** (only if richer auditing needed): `ChatGptAccountHealth` (per-account
success/fail counters, last-429, cooldown-until). Otherwise reuse the existing `RequestLogs` /
`SlaMetrics` and an in-memory cooldown in `AccountPool`.

**Migration note (gateway-host):** per the deploy skill, EF migrations are now the supported path
(`MigrateAsync` at boot). Any *new* column uses `dotnet ef migrations add`; the `Label` column and
`OAuthPendingFlow.AiProviderCode` already exist on the live schema. Adding a new entity = one migration
+ tar/scp/docker build/deploy.

---

## 9. API Design (adapt to existing conventions)

Management endpoints (auth-exempt callback; rest behind `ADMIN_API_KEY` or Admin cookie; add `/oauth/*`
and `/admin/chatgpt/*` to the `ApiKeyAuthMiddleware` denylist in `Program.cs` exactly like the existing
Gemini OAuth):

- `POST /admin/chatgpt/accounts` — register a new ChatGPT account (creates `chatgpt-accN` provider +
  links OAuth config). Returns the account id/code.
- `POST /oauth/chatgpt/start` — begin OAuth (returns authorize URL or device user_code).
- `GET /oauth/chatgpt/callback` — provider redirect target (AllowAnonymous; reuse existing pattern).
- `POST /oauth/chatgpt/disconnect` — revoke token + flip provider to disabled.
- `GET /oauth/chatgpt/status` — per-account status (Connected/Expired/Error + ExpiresAt + LastError).
- `GET /admin/chatgpt/accounts` — list accounts (id, display name, status, last success, enabled).
- `POST /admin/chatgpt/accounts/{id}/enable` / `/disable`.
- `POST /admin/chatgpt/accounts/{id}/reauthenticate` — restart OAuth for that account.
- `DELETE /admin/chatgpt/accounts/{id}` — remove account + token.
- `GET /admin/chatgpt/health` — pool health (per-account success/fail, cooldown state).

---

## 10. Management UI

Blazor Server pages (reuse `Modal.razor`, `Material Symbols`, the existing OAuth "Connections" card
pattern from the Gemini OAuth work):

- **ChatGPT Accounts** page: card grid, one card per `chatgpt-accN` showing display name (derived from
  `chatgpt_account_id`/`Label`), status pill (Connected/Expired/Error), last-success timestamp,
  token-expiry, enabled toggle, Re-authenticate + Remove buttons.
- **Add Account** modal: starts the OAuth flow, shows the device user-code or the authorize link, polls
  status until Connected.
- **Health** section: per-account success/fail counts, current cooldown state, pool selection mode
  (round-robin / LRU / health-aware) toggle.
- Expose **metadata only** (status, names, timestamps, errors) — never raw tokens.

---

## 11. Security Model

**OAuth**
- Authorization-Code + PKCE (S256) — already implemented in `OAuthFlowService`/`BuildAuthUrl`.
- Cryptographically random `state` (CSRF) — already persisted in `OAuthPendingFlow` and validated at
  callback.
- Exact `redirect_uri` validation — already enforced (`flow.RedirectUri` checked at exchange).
- No authorization-code leakage — code exchanged server-side, never returned to UI.
- Secure callback — `AllowAnonymous` only on the callback route, which immediately exchanges the code.

**Credential storage (already meets requirements)**
- Envelope encryption at rest (`EnvelopeCredentialVault`, AES-256-GCM, per-secret DEK). Master key via
  `ARKANA_MASTER_KEY` / file / auto-gen. **Pin the master key on gateway-host** (per `arkana-gateway-ops`
  skill) so a rebuild does not orphan sealed tokens.
- Access + refresh tokens sealed in `ProviderOAuthToken`; decrypted just-in-time.
- Tokens never in logs / HTTP logs / errors / frontend. (Audit `ChatGptCodexChatService` to ensure the
  `ChatGPT-Account-Id` and bearer are not logged; the existing `RequestLogs` stores metadata only.)

**Management UI**
- Admin authentication required (cookie Admin session OR `ADMIN_API_KEY`). Add `/oauth/chatgpt/*` and
  `/admin/chatgpt/*` to the `Program.cs` denylist.
- RBAC: only Admin can register/remove accounts.
- Audit logging: every register/auth/revoke/remove recorded (use existing audit path).

**Refresh-token safety**
- Codex refresh tokens are single-use; the gateway must **own** each account's token set exclusively
  (do not let a user's Codex CLI share it → `refresh_token_reused`).
- Detect `refresh_token_reused` / `invalid_grant` → mark account Error + require re-auth (do not
  silently retry forever).

---

## 12. Risks and Constraints

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R1 | **Reusing OpenAI's first-party `client_id`** (`app_EMoamEEZ73f0CkXaXp7hrann`) — we did not register it; OpenAI may rotate/revoke or tighten `redirect_uri` | HIGH | Document as accepted risk; isolate behind a config so the client_id is swappable; monitor for auth failures; have a fallback (ask owner to register a first-party app / use device flow). |
| R2 | **ChatGPT subscription ≠ API platform.** Token only authorizes Codex/subscription models on `chatgpt.com/backend-api/codex`, not `api.openai.com/v1` catalog | HIGH | Model remap adapter; reject non-Codex models with clear error; never route generic `/v1` calls expecting `gpt-4o` to this account. |
| R3 | **Device-code / localhost callback** needs interactive capture; gateway is headless behind VPN | MED | Prefer the **device-code** method (no localhost bind); gateway polls `auth.openai.com/api/accounts/deviceauth/token` and captures code+verifier. For the browser method, proxy the callback to the gateway's public `/oauth/chatgpt/callback`. |
| R4 | **Single-use refresh token** shared across clients → `refresh_token_reused` | MED | Gateway exclusively owns each account token; do not document/encourage sharing with Codex CLI. |
| R5 | **Quota is opaque (HTTP 429 = usage, not auth)** | MED | Router treats 429 as cooldown + try-next-account, not re-auth (mirror Hermes `CODEX_RATE_LIMITED_CODE`). |
| R6 | **`ChatGPT-Account-Id` header required** or Codex returns empty model list / wrong context | MED | Always inject header from JWT claim (Hermes pattern). |
| R7 | **ToS / stability of `chatgpt.com/backend-api/codex`** — less documented than Platform API | MED | Treat as "technically possible but not a guaranteed public API"; sandbox to a clearly-labeled feature; monitor. |
| R8 | Master-key rotation orphans sealed tokens | MED | Pin `ARKANA_MASTER_KEY` on gateway-host; null-and-reseal recovery path exists. |
| R9 | Gateway is the token broker → high-value target | HIGH | Envelope encryption + ADMIN_AUTH + audit + never log tokens; consider separating the OAuth secret store. |

---

## 13. Implementation Plan (ordered phases)

> All phases build on the EXISTING OAuth framework. Each phase: branch off `main` → code → `dotnet
> build -c Release` + tests green → PR → bundle/SSH deploy to gateway-host (no production breakage).

**Phase 1 — Infrastructure prep.** Add `ChatGptOAuthConfig` seed (env-driven client_id, token/device
endpoints) via `OAuthConfigSeeder`. Add `/oauth/chatgpt/*` + `/admin/chatgpt/*` to `Program.cs`
denylist. *Validation:* `GET /oauth/configs` lists `chatgpt`.

**Phase 2 — ChatGpt account provider + OAuth wiring.** `AdminEndpoints`: `POST /admin/chatgpt/accounts`
creates `chatgpt-accN` (`AuthMethod=OAuth`, `OAuthConfigId`, `BaseUrl=https://chatgpt.com/backend-api/codex`).
Extend `OAuthFlowService` to support the **device-code** grant (server polls `deviceauth/token`, captures
`authorization_code` + `code_verifier`). *Validation:* live device-code login from a test ChatGPT account
→ `ProviderOAuthToken.Status=Connected`.

**Phase 3 — Connector + header injection.** `ChatGptCodexChatService : OpenAiCompatChatServiceBase`
(`Endpoint=/codex` or `/responses`). Inject `ChatGPT-Account-Id` from JWT claim; pin `User-Agent`/
`originator`. *Validation:* a `curl`/agent call with the OAuth bearer + header returns a Codex completion.

**Phase 4 — Model adapter / remap.** Map `/v1` model names → Codex catalog; reject non-Codex models
with clear error (mirror opencode pre-flight remap). *Validation:* requesting a Codex model works; a
non-Codex model returns a clear 4xx, not a silent upstream 401.

**Phase 5 — Account pool + health-aware router.** `AccountPool` service: registry of `chatgpt-acc*`
(health, ExpiresAt, enabled, cooldown, `ChatGPT-Account-Id`), selection strategy (round-robin/LRU/
health-aware), 429 cooldown + try-next. Wire into `SendChatHandler`/`ChatEndpoints` so a `/v1` request
picks an account. *Validation:* with 2 accounts, simulate one 429 → traffic shifts; cooldown respected.

**Phase 6 — Management UI.** Blazor "ChatGPT Accounts" page (cards, add-account modal, enable/disable,
re-auth, remove, health). *Validation:* full cycle in the browser; tokens never shown.

**Phase 7 — Security hardening + audit.** Admin-auth gate, audit log of register/auth/revoke/remove,
ensure no token/header leakage in `RequestLogs`. Pin master key on gateway-host. *Validation:* `ADMIN_API_KEY`
required; grep logs for token/account-id absence.

**Phase 8 — Testing.** Unit (PKCE/state/exchange/refresh, single-use refresh, 429 cooldown), integration
(device-code mock or sandbox account), multi-account rotation, failure (expired/invalid_grant/revoked/
network timeout/VPN flap/duplicate account/concurrent refresh), security (no token in logs/responses),
production smoke (one real ChatGPT account end-to-end).

**Phase 9 — Deployment.** Isolated branch → bundle to gateway-host → `docker build --no-cache` → surge →
health + `/oauth/configs` + one live account end-to-end. Keep old provider routing intact (backward
compatible).

**Phase 10 — Monitoring & rollback.** Health checks (per-account `Connected`/Expiry), alert on mass
`Expired`/`Error`, SLA metrics. Rollback = revert image to previous tag (OAuth rows are additive; old
providers untouched). DB additive-only; if a migration was added, a down-migration reverts it.

---

## 14. Test Strategy

- **Unit:** `OAuthFlowService` PKCE/state/exchange/refresh; `ChatGptCodexChatService` header injection +
  claim extraction; `AccountPool` selection + cooldown + single-use refresh detection.
- **Integration:** device-code against a sandbox/real ChatGPT account (or a recorded token); full
  `start → callback → Connected → request` loop.
- **OAuth flow:** authorization-code + device-code; expired `state`; `redirect_uri` mismatch.
- **Token refresh:** `expires_in` slack refresh; `refresh_token_reused`/`invalid_grant` → Error+reauth;
  `429` → cooldown (NOT reauth).
- **Multi-account:** 2 accounts, one 429 → failover; round-robin distribution; duplicate-account
  detection (same `chatgpt_account_id`).
- **Failure:** expired access; expired refresh; revoked; account logout; invalid state; callback
  failure; provider outage; rate limit; quota; malformed upstream; network timeout; VPN failure;
  concurrent auth; concurrent refresh (race); partially completed flow; DB failure; key failure.
- **Security:** tokens absent from logs/HTTP/errors/frontend; admin-auth required on management APIs;
  CSRF state validated; exact redirect URI.
- **Smoke:** one real ChatGPT account end-to-end on `gateway.arkana.dev`.

---

## 15. Rollback Plan

- **Code:** deploy from a prior image tag; the OAuth additions are additive (new `OAuthProviderConfig`
  row, new `chatgpt-acc*` providers, new `ProviderOAuthToken` rows). Removing the feature = disable the
  `chatgpt` config + `chatgpt-acc*` providers (no data loss); old providers (opencode/gemini/…) are
  untouched.
- **DB:** if a migration was introduced, apply its down-migration. All new columns/tables are additive;
  dropping them is safe.
- **Secrets:** client_id is not a secret (public client). No secret rotation needed for rollback.
- **Verify rollback:** health + `/v1/chat/completions` on existing providers green; `chatgpt` routes
  gone/disabled.

---

## 16. Execution Checklist (sequential, for the implementer)

1. [ ] Branch `feat/chatgpt-oauth` off `main`; confirm `dotnet build -c Release` green on baseline.
2. [ ] Add `chatgpt` `OAuthProviderConfig` seed (env-driven client_id) in `OAuthConfigSeeder`; extend
   `OAuthProviderConfig` entity if a `GrantType.CodexDevice` enum value is needed.
3. [ ] Add `/oauth/chatgpt/*` and `/admin/chatgpt/*` to `Program.cs` `ApiKeyAuthMiddleware` denylist.
4. [ ] Extend `OAuthFlowService` for device-code grant (server polls `deviceauth/token`, captures
   code+verifier); keep PKCE/state/exact-redirect behavior.
5. [ ] `POST /admin/chatgpt/accounts` in `AdminEndpoints` → creates `chatgpt-accN` (OAuth + config link +
   BaseUrl). Add a `CreateOAuthAccountAsync` auto-numbering helper (mirror Gemini `accN`).
6. [ ] `ChatGptCodexChatService : OpenAiCompatChatServiceBase` → `Endpoint=/codex` (or `/responses`),
   inject `ChatGPT-Account-Id` from JWT claim, pin `User-Agent`/`originator`.
7. [ ] Model remap adapter for the Codex catalog; clear error for non-Codex models.
8. [ ] `AccountPool` service (health, cooldown, select strategy) + wire into routing.
9. [ ] Blazor "ChatGPT Accounts" page + Add-Account modal + health view.
10. [ ] Admin-auth + audit logging; verify no token/header leakage in logs.
11. [ ] Unit + integration + multi-account + failure + security tests green.
12. [ ] Deploy: bundle → gateway-host → `docker build --no-cache` → surge → health + `/oauth/configs` +
    one live account E2E.
13. [ ] Pin `ARKANA_MASTER_KEY` on gateway-host; verify sealed-token durability across rebuild.
14. [ ] Document the accepted risk (R1: first-party client_id reuse) in the repo ADR.

---

## 17. Research Sources

- Gateway repo `C:/Workspace/arkana-gateway` @ `c3cc470` (main), files:
  - `src/Arkana.Infrastructure/OAuth/OAuthFlowService.cs`
  - `src/Arkana.Gateway.Api/Endpoints/OAuthEndpoints.cs`
  - `src/Arkana.Infrastructure/OAuth/OAuthTokenResolver.cs`
  - `src/Arkana.Infrastructure/OAuth/OAuthTokenRefreshService.cs`
  - `src/Arkana.Infrastructure/AI/OpenAiCompatChatServiceBase.cs`
  - `src/Arkana.Domain/Entities/{OAuthProviderConfig,ProviderOAuthToken,OAuthPendingFlow,AiProvider}.cs`
  - `src/Arkana.Infrastructure/Security/EnvelopeCredentialVault.cs`
  - `src/Arkana.Gateway.Api/Program.cs`, `src/Arkana.Gateway.Api/Endpoints/AdminEndpoints.cs`
  - `CLAUDE.md` (auth/middleware architecture)
- OpenCode `github.com/anomalyco/opencode` @ `e2505d434a6d78904ecfe546c4a1980d26bd8cd1`:
  - `packages/core/src/plugin/provider/openai.ts` (v2 ChatGPT OAuth: PKCE, state, browser+headless
    device methods, `client_id=app_EMoamEEZ73f0CkXaXp7hrann`, `auth.openai.com`, account-id claim).
  - `packages/opencode/src/provider/auth.ts` (generic OAuth framework).
- Hermes Agent `github.com/NousResearch/hermes-agent` @ `ce9d48ce85ce9d7d20ded8c406bea8ef89aa14b3`:
  - `hermes_cli/auth.py` (`CODEX_OAUTH_CLIENT_ID`, `CODEX_OAUTH_TOKEN_URL`, `refresh_codex_oauth_pure`).
  - `agent/credential_pool.py` (`openai-codex`, `base_url=https://chatgpt.com/backend-api/codex`,
    multi-entry pool + cooldown).
  - `agent/auxiliary_client.py` / `agent/model_metadata.py` (`chatgpt_account_id` claim →
    `ChatGPT-Account-Id` header).
- OpenAI auth model (from direct knowledge + verified in source above): ChatGPT subscription access via
  OpenAI's first-party **Codex OAuth** (`auth.openai.com`), token used against `chatgpt.com/backend-api/codex`.
  Distinct from the OpenAI **Platform API** (`api.openai.com/v1`, API keys). (Web search/extract tooling
  was unavailable this session — PARALLEL_API_KEY unset — so OpenAI-doc claims are grounded via the
  reference implementations' verified constants/endpoints rather than live doc fetches; flagged as a
  verification gap.)

---

## Progress Log (chronological)

| Timestamp (UTC+7) | Component | Finding | Evidence | Implication | Confidence |
|---|---|---|---|---|---|
| 2026-08-20 | Gateway repo | Gateway already has server-orchestrated OAuth+PKCE+sealed tokens | `OAuthFlowService.cs`, `OAuthPendingFlow.cs` | Reuse, don't rebuild | FACT |
| 2026-08-20 | Gateway repo | Multi-account scaffolding exists (`accN` codes, `Label`, `OAuthConfigId`) | `AiProvider.cs`, `ProviderOAuthToken.cs` | Account pool mostly a routing addition | FACT |
| 2026-08-20 | OpenCode | ChatGPT auth = OAuth Auth-Code+PKCE @ `auth.openai.com`, client_id `app_EMoamEEZ73f0CkXaXp7hrann` | `packages/core/src/plugin/provider/openai.ts` @ `e2505d4` | Use same endpoints; it's OpenAI's first-party Codex client | FACT |
| 2026-08-20 | OpenCode | Two methods: browser (localhost:1455) + headless device-code | same file | Gateway (headless) should use device-code or proxy callback | FACT |
| 2026-08-20 | Hermes | Identical client_id + `chatgpt.com/backend-api/codex` base, `ChatGPT-Account-Id` header from claim | `hermes_cli/auth.py`, `agent/credential_pool.py`, `agent/auxiliary_client.py` @ `ce9d48c` | Mirror header injection + multi-account pool/cooldown | FACT |
| 2026-08-20 | Both | Refresh tokens single-use; 429 = quota not auth | `refresh_codex_oauth_pure`, OpenCode refresh | Gateway must own token; router cools down on 429 | FACT |
| 2026-08-20 | OpenAI model | ChatGPT subscription ≠ Platform API; Codex catalog only | source constants + model_metadata comments | Need adapter/remap; document R2 | INFERENCE (high) |
| 2026-08-20 | OpenAI ToS | Reusing first-party client_id is undocumented reliance | both repos hardcode it | R1 risk; flag to owner | ASSUMPTION (reasonable) |
| 2026-08-20 | Web tooling | web_search/web_extract unavailable (PARALLEL_API_KEY unset) | tool errors | OpenAI-doc claims grounded via source, not live docs | OPEN QUESTION |

**Do not implement yet.** This is the Phase-1 deliverable. Implementation begins only after review/approval.
