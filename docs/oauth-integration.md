# OAuth Provider Integration for the Gateway `/providers` Page

**Status:** Implementation plan (documentation-first)
**Date:** 2026-07-20
**Scope:** Add **OAuth (not only API Key)** as a selectable auth method for AI providers, so provider credentials can be distributed/integrated via interactive OAuth (device-code / authorization-code) instead of pasting a static key. Realizes FR-3 / P1-3 from `GATEWAY-SRS-EXECUTION-PLAN.md`.

---

## 1. Goal

Today `AiProvider` only supports a static API key (`ApiKey`, sealed). The `/providers` page (`Providers.razor`) lets an admin create/edit a provider with Name/Code/BaseUrl/**API Key** only.

We add a **second auth method: OAuth**. When a provider uses OAuth, the gateway:
1. Runs an interactive OAuth flow (device-code or authorization-code) initiated from the `/providers` page.
2. Stores the resulting tokens **sealed** (reusing the existing envelope encryption — `ICredentialVault.Seal/Open`), tenant-scoped.
3. Auto-refreshes the access token before expiry via a background `IHostedService`.
4. Injects the live access token as `Authorization: Bearer` on outbound requests (instead of the static API key).
5. Exposes status in the UI (connected / expired / needs re-auth).

Net effect: a provider entry is **distributed** to tenants/clients as "connect via OAuth" — the gateway brokers the credential, with envelope encryption + tenant isolation.

---

## 2. Design Decisions & Constraints

- **Reuse the chassis (DC-1 / NFR-1/NFR-2):** OAuth tokens stored sealed via existing `ICredentialVault`; all new tables tenant-scoped (`TenantId`); outbound calls still go through the SSRF guard (BaseUrl is validated at provider creation, same as today).
- **Auth method is a discriminator on `AiProvider`:** add `AuthMethod` enum (`ApiKey`, `OAuth`). No provider needs both simultaneously at v1. `ApiKey` stays the default → **100% backward compatible** with seeded providers.
- **OAuth provider metadata is separate (`OAuthProviderConfig`):** holds the client id/secret + endpoints + grant type per `provider_code`. One template per OAuth-capable platform; many `AiProvider` rows can reference it. Client secret stored **sealed**.
- **Per-connection token state (`ProviderOAuthToken`):** tenant-scoped row per `AiProvider` that chose OAuth; holds sealed access/refresh tokens + expiry + status.
- **Live DB uses `EnsureCreatedAsync()` (no EF migrations)** — confirmed in `AdminUserSeeder.cs`. New tables/columns therefore need a **manual DDL step** at deploy time (runbook in §8). This is the same constraint noted in prior gateway ops (schema change = direct SQL on `arkana-postgres`).
- **Auto-refresh** near expiry (e.g. refresh when `< 5 min` remaining) or on first failure with `invalid_token`/`401`.

---

## 3. Data Model

### 3.1 `AiProvider` (extend existing entity)
Add:
```csharp
public AuthMethod AuthMethod { get; private set; } = AuthMethod.ApiKey;
public Guid? OAuthConfigId { get; private set; }   // set when AuthMethod == OAuth
public bool UsesOAuth => AuthMethod == AuthMethod.OAuth;
```
- `UpdateAuthMethod(AuthMethod, oauthConfigId?)` mutator.
- `DecryptApiKey` untouched (used only in API-key mode).

### 3.2 `AuthMethod` enum (Domain)
```csharp
public enum AuthMethod { ApiKey = 0, OAuth = 1 }
```

### 3.3 `OAuthProviderConfig` (new, Domain entity — *template*, not tenant-scoped secrets per-provider)
| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| ProviderCode | string(64) | e.g. `openrouter`, `anthropic`, `openai`, `github-models` |
| DisplayName | string(128) | "OpenRouter (OAuth)" |
| GrantType | `OAuthGrant` enum | `DeviceCode`, `AuthorizationCode` |
| AuthorizationEndpoint | string | — |
| TokenEndpoint | string | — |
| DeviceAuthorizationEndpoint | string? | device-code flow |
| ClientId | string(512) | plaintext (public client) |
| SealedClientSecret | string? | sealed; null for public clients |
| Scopes | string(512) | space-separated |
| ExtraAuthParams | string? | JSON, provider-specific (e.g. `audience`) |

### 3.4 `ProviderOAuthToken` (new, Domain entity — *per-connection*, tenant-scoped)
| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| AiProviderId | Guid | FK → AiProvider |
| TenantId | Guid | tenant isolation |
| SealedAccessToken | string(4096) | sealed |
| SealedRefreshToken | string(4096)? | sealed; null if not issued |
| TokenType | string(32) | "Bearer" |
| ExpiresAt | DateTimeOffset? | absolute expiry of access token |
| RefreshExpiresAt | DateTimeOffset? | optional |
| Status | `OAuthTokenStatus` enum | `Pending`, `Connected`, `Expired`, `Error`, `Revoked` |
| LastError | string? | |
| CreatedAt / UpdatedAt | DateTimeOffset | |

`OAuthTokenStatus` enum: `Pending=0, Connected=1, Expired=2, Error=3, Revoked=4`.

---

## 4. Application / Infrastructure Services

### 4.1 `IOAuthFlowService` (Infrastructure/OAuth)
- `StartFlowAsync(Guid providerId, CancellationToken)` → returns `{ DeviceCode, UserCode, VerificationUri, ExpiresIn, Interval, PollingHandle }` for device-code, or `{ AuthorizationUrl, State, CodeVerifier, PollingHandle }` for auth-code (PKCE). Persists a `ProviderOAuthToken` row in `Pending`.
- `PollAsync(Guid providerId, string handle, CancellationToken)` → exchanges code for tokens, seals + persists, sets `Connected`. Returns status.
- `GetStatusAsync(Guid providerId)` → current status (for UI + connector).
- `DisconnectAsync(Guid providerId)` → sets `Revoked`, clears sealed tokens.
- `GetValidAccessTokenAsync(Guid providerId, CancellationToken)` → returns decrypted access token, **refreshing first if within 5 min of expiry** (or on demand). This is what the connector calls.

### 4.2 `OAuthTokenRefreshService : IHostedService` (Infrastructure)
- Periodically (every 60s) scans `ProviderOAuthToken` rows with `Status == Connected` whose `ExpiresAt` is within 5 min → calls `IOAuthFlowService.RefreshAsync`.
- Swallows/logs per-row errors; never throws out of the loop.

### 4.3 Token storage
- All token/secret bytes sealed via `ICredentialVault.Seal` (AES-256-GCM envelope, same as `ApiKey`). Decrypt just-in-time inside `GetValidAccessTokenAsync` and refresh.

---

## 5. Connector Integration (minimal, chassis-respecting)

`OpenAiCompatChatServiceBase.CompleteAsync` currently:
```csharp
var provider = await _catalog.GetByCodeAsync(ProviderCode, ct);
string? apiKey = provider?.DecryptApiKey(_vault) ?? envFallback ?? "";
```
Change: after resolving `provider`, if `provider.UsesOAuth`, resolve the live bearer via an injected `IOAuthTokenResolver.GetValidAccessTokenAsync(provider.Id)` instead of `DecryptApiKey`. The resolver wraps `IOAuthFlowService.GetValidAccessTokenAsync` and is DI-injected into the base class (guarded for null in tests). Static-API-key path **unchanged**.

`IOAuthTokenResolver` (thin interface in Domain.Interfaces) exists purely so the connector layer doesn't depend on the OAuth service directly (keeps the connector unit-testable with a stub).

---

## 6. API Surface (IMPLEMENTED)

### 6.1 `OAuthEndpoints` (new, under `/oauth`; shares dashboard auth with `/admin`)
| Method | Route | Purpose |
|---|---|---|
| GET | `/oauth/providers` | list seeded `OAuthProviderConfig`s (code, display, grant) for the picker |
| POST | `/oauth/start` | body `{ providerId }` → start flow, return device code / auth URL + handle |
| GET | `/oauth/callback` | authorization-code redirect target (AllowAnonymous); receives `code`+`state` |
| GET | `/oauth/status/{providerId}` | current connection status (UI polling) |
| POST | `/oauth/poll` | body `{ providerId, handle }` → exchange + persist; returns status |
| POST | `/oauth/disconnect` | body `{ providerId }` → revoke + clear tokens |

### 6.2 `AdminEndpoints` (extended)
- `GET /admin/providers` response gains `AuthMethod`, `OAuthConfigId`, `OAuthConfigCode`.
- `PUT /admin/providers/{id}/auth-method` body `{ authMethod, oAuthConfigId? }` → switch a provider between `ApiKey` and `OAuth` (validates `oAuthConfigId` required for OAuth). Calls `catalog.Invalidate()`.

> Note: implementation uses `OAuthGrant`/`OAuthTokenStatus` enums (not `string`); the UI compares `.ToString()`.

---

## 7. UI — `Providers.razor` (EXTENDED)

- **Create/Edit modal:** add an **Auth Method** selector: `API Key` | `OAuth`.
  - *API Key* → existing API Key field.
  - *OAuth* → a dropdown of seeded `OAuthProviderConfig` (OpenRouter, Anthropic, OpenAI, GitHub Models…) instead of a key field.
- **Provider row:** when `AuthMethod == OAuth`, show an auth status chip (`Connected` / `Expired` / `Needs re-auth`) instead of the key glyph.
- **"Connect via OAuth" button** (when OAuth + not connected): opens a modal that calls `/oauth/start`, displays the device code / verification URL (big, copyable), then polls `/oauth/status` until `Connected` or timeout. On connected, refreshes the table.
- **Disconnect** action for OAuth providers.

---

## 8. Deployment / DDL Runbook (live Postgres `arkana-postgres`)

Because the running server uses `EnsureCreatedAsync()` and **there is no EF migration pipeline**, the new tables + `AiProviders` columns must be created by direct DDL **before** the new image serves traffic.

> **Use the authoritative, EF-generated script `oauth-schema.sql`** (committed at repo root). It was produced with
> `dotnet ef dbcontext script --context GatewayDbContext` and is guaranteed to match the EF model
> (correct quoted identifiers + enum columns stored as `integer`). The earlier hand-written DDL in this doc
> caused casing/`varchar`-vs-`integer` mismatches — do **not** re-apply it.

Apply on the live Postgres (e.g. `docker exec -i arkana-postgres psql -U arkana -d arkana -f oauth-schema.sql`):

```sql
-- oauth-schema.sql (EF-generated, idempotent: drops + recreates the two OAuth tables,
-- adds AuthMethod/OAuthConfigId to AiProviders with FK + index)
DROP TABLE IF EXISTS "ProviderOAuthTokens";
DROP TABLE IF EXISTS "OAuthProviderConfigs";

CREATE TABLE "OAuthProviderConfigs" (
    "Id" uuid NOT NULL,
    "ProviderCode" character varying(64) NOT NULL,
    "DisplayName" character varying(128) NOT NULL,
    "GrantType" integer NOT NULL,                 -- OAuthGrant enum, stored as int
    "AuthorizationEndpoint" character varying(1024),
    "TokenEndpoint" character varying(1024) NOT NULL,
    "DeviceAuthorizationEndpoint" character varying(1024),
    "ClientId" character varying(512) NOT NULL,
    "SealedClientSecret" character varying(4096),
    "Scopes" character varying(512),
    "ExtraAuthParams" text,
    CONSTRAINT "PK_OAuthProviderConfigs" PRIMARY KEY ("Id")
);

CREATE TABLE "ProviderOAuthTokens" (
    "Id" uuid NOT NULL,
    "AiProviderId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "SealedAccessToken" character varying(4096),
    "SealedRefreshToken" character varying(4096),
    "TokenType" character varying(32) NOT NULL,
    "ExpiresAt" timestamp with time zone,
    "RefreshExpiresAt" timestamp with time zone,
    "Status" integer NOT NULL,                    -- OAuthTokenStatus enum, stored as int
    "LastError" text,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ProviderOAuthTokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_ProviderOAuthTokens_AiProviders_AiProviderId" FOREIGN KEY ("AiProviderId") REFERENCES "AiProviders" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ProviderOAuthTokens_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT
);

ALTER TABLE "AiProviders" ADD COLUMN IF NOT EXISTS "AuthMethod" integer NOT NULL DEFAULT 0;  -- AuthMethod enum
ALTER TABLE "AiProviders" ADD COLUMN IF NOT EXISTS "OAuthConfigId" uuid;
ALTER TABLE "AiProviders" ADD CONSTRAINT "FK_AiProviders_OAuthProviderConfigs_OAuthConfigId"
    FOREIGN KEY ("OAuthConfigId") REFERENCES "OAuthProviderConfigs" ("Id") ON DELETE RESTRICT;
CREATE INDEX IF NOT EXISTS "IX_AiProviders_OAuthConfigId" ON "AiProviders" ("OAuthConfigId");
```

Seeding `OAuthProviderConfigs` (client id/secret) is done by `OAuthConfigSeeder` at startup (it calls `ICredentialVault.Seal`, so secrets are stored sealed — never put plaintext in SQL). The seeder tolerates unset `OAUTH_*_CLIENT_ID` env vars (registers the template; real creds supplied later).

> Note: enum columns are stored as `integer` (EF default), not `varchar`. `EnsureCreatedAsync()` only creates missing tables and will **not** add columns to the existing `AiProviders` — hence the explicit `ALTER`.

---

## 9. Tests (maintain 841+ discipline, zero warnings)

- **Domain:** `AiProvider` `AuthMethod`/`UsesOAuth` transitions; `OAuthProviderConfig`/`ProviderOAuthToken` factory + sealed-token round-trip via a fake `ICredentialVault`.
- **Unit:** `OAuthTokenRefreshService` picks rows `< 5 min` to expiry, calls refresh; skips others; never throws.
- **Unit:** `IOAuthTokenResolver` returns `null` when provider is API-key mode (connector keeps using `DecryptApiKey`).
- **Integration (TestServer):** `POST /oauth/start` → `POST /oauth/poll` with a mocked token endpoint → `GET /oauth/status` returns `Connected`; a subsequent `GET /admin/providers` reflects `AuthMethod=OAuth`; outbound request from `OpenAiCompatChatServiceBase` uses the sealed→decrypted access token (assert header).
- **Fail-safe:** if OAuth token expired and refresh fails, request returns a clear 502/401 with provider status, never a plaintext leak.

---

## 10. Acceptance Criteria

1. `/providers` page lets an admin pick **OAuth** as the auth method and choose a seeded OAuth config.
2. "Connect via OAuth" runs the flow; user authorizes; token stored **sealed**, `Status=Connected`.
3. Outbound requests to that provider use the OAuth `Bearer` token (not an API key).
4. Access token auto-refreshes before expiry; UI shows live status.
5. Switching a provider back to API Key clears OAuth state; existing API-key providers unaffected.
6. Tokens tenant-isolated; all new tables tenant-scoped; zero build warnings; tests green.

---

## 11. Execution Order

1. Domain: `AuthMethod` enum, `AiProvider` extension, `OAuthProviderConfig` + `ProviderOAuthToken` entities, `OAuthTokenStatus`/`OAuthGrant` enums, `IOAuthTokenResolver`.
2. Infrastructure: DbContext DbSets + model config + snapshot; `IOAuthFlowService` + `OAuthTokenRefreshService`; `OAuthConfigSeeder`.
3. Connector: `OpenAiCompatChatServiceBase` OAuth bearer path via `IOAuthTokenResolver`.
4. API: `OAuthEndpoints` + `AdminEndpoints` extensions.
5. UI: `Providers.razor` OAuth controls + connect modal.
6. Tests (domain + integration).
7. Build + `dotnet test`; fix to green, zero warnings.
8. Docs + DDL runbook (this file §8); report.
