# OAuth Token Pool — Execution Plan

**Goal:** 1 company (tenant) manages 7 Google OAuth accounts, distributable to employees via API keys  
**Design principle:** Zero regression for existing single-account OAuth flow. All changes backward-compatible.  
**Status:** Reviewed and approved for execution.

---

## Current Architecture

```
OAuthProviderConfig (code: "gemini")              ← 1 Google OAuth app
  └── AiProvider (code: "gemini")                 ← references OAuthConfigId
        └── ProviderOAuthToken (1 token)          ← bound by AiProviderId
              └── Model (code: "gemini-2.0-flash") → points to AiProvider
```

**Constraint:** 1 AiProvider → 1 OAuth token. No label. No multi-account.

---

## Target Architecture

```
OAuthProviderConfig (code: "gemini")              ← SAME Google OAuth app
  ├── AiProvider (code: "gemini-acc1")            ← references OAuthConfigId
  │     └── ProviderOAuthToken { Label: "Budi" }  ← unique token per account
  │           └── Model "gemini-2.0-flash-acc1"
  ├── AiProvider (code: "gemini-acc2")
  │     └── ProviderOAuthToken { Label: "Sinta" }
  │           └── Model "gemini-2.0-flash-acc2"
  ├── ... up to acc7
  │
  └── API Key "Budi"   → allowed models: [gemini-2.0-flash-acc1]
      API Key "Sinta"  → allowed models: [gemini-2.0-flash-acc2]
      API Key "Dev"    → allowed models: [acc3, acc4, acc5]
```

**Key insight:** Setiap AiProvider punya kode unik (`gemini-acc1`..`gemini-acc7`) + referensi ke OAuth config yang sama (`OAuthConfigId`). Masing2 punya `ProviderOAuthToken` sendiri.

---

## Execution Checklist

### Phase A — Data Model: Label on `ProviderOAuthToken`

**Files:** `ProviderOAuthToken.cs`, `IOAuthRepositories.cs`, `OAuthRepositories.cs`

| # | Task | File | Detail | Regression Check |
|---|---|---|---|---|
| A1 | Add `Label` property | `Domain/Entities/ProviderOAuthToken.cs` | `public string? Label { get; private set; }` | `null` for existing rows ✅ |
| A2 | Update `CreatePending` factory | `Domain/Entities/ProviderOAuthToken.cs` | Add `string? label = null` param | Default null = unchanged callers ✅ |
| A3 | Add `GetAllByProviderAsync` to interface | `Domain/Interfaces/IOAuthRepositories.cs` | `Task<IReadOnlyList<ProviderOAuthToken>> GetAllByProviderAsync(Guid, CancellationToken)` | New method — no callers affected ✅ |
| A4 | Implement `GetAllByProviderAsync` | `Infrastructure/.../OAuthRepositories.cs` | `.Where(t => t.AiProviderId == providerId).ToListAsync()` | ✅ |
| A5 | Verify EF convention for `Label` | `Infrastructure/.../GatewayDbContext.cs` | Nullable string — convention handles it | No config needed ✅ |

### Phase B — Multi-Account OAuth Flow

**Challenge:** Google redirect URI must match registered URI exactly. We have 1 registered URI:  
`https://gateway.arkana.dev/oauth/gemini/callback`  
Need to bind callback to the correct `AiProvider` when multiple exist.

**Solution:** Store target `AiProviderCode` in `OAuthPendingFlow`. Redirect URI always uses config code (`gemini`). Callback reads stored code to bind tokens.

**Files:** `OAuthPendingFlow.cs`, `OAuthFlowService.cs`

| # | Task | File | Detail | Regression Check |
|---|---|---|---|---|
| B1 | Add `AiProviderCode` property | `Domain/Entities/OAuthPendingFlow.cs` | `public string? AiProviderCode { get; }` | `null` = original behavior ✅ |
| B2 | Update `OAuthPendingFlow.Create` | `Domain/Entities/OAuthPendingFlow.cs` | Add `string? aiProviderCode = null` param | Default null ✅ |
| B3 | Refactor `StartAsync` — resolve provider first | `Infrastructure/OAuth/OAuthFlowService.cs` | Try `_providers.GetByCodeAsync(code)` → if `OAuthConfigId` set, load config by ID. Fallback: `_configs.GetByCodeAsync(code)`. Store target in `AiProviderCode`. Use config code for redirect path. | Fallback preserves existing path ✅ |
| B4 | Refactor `HandleCallbackAsync` — use stored code | `Infrastructure/OAuth/OAuthFlowService.cs` | Token binding: `flow.AiProviderCode ?? providerCode` | Null fallback = original ✅ |
| B5 | Verify redirect URI uses config code | `Infrastructure/OAuth/OAuthFlowService.cs` | `redirectUri = "{base}/oauth/{cfg.ProviderCode}/callback"` | ✅ |

#### B3 Detailed Logic (StartAsync)

```
POST /oauth/gemini-acc1/start

1. _providers.GetByCodeAsync("gemini-acc1")  →  AiProvider with OAuthConfigId
2. _configs.GetByIdAsync(provider.OAuthConfigId)  →  OAuthProviderConfig (code: "gemini")
3. Build PKCE + state
4. redirectUri = "{base}/oauth/gemini/callback"   ← config code, NOT provider code
5. pendingFlow.AiProviderCode = "gemini-acc1"      ← stored for callback
6. authUrl = Google's authorization URL with redirect_uri = above
```

```
GET /oauth/gemini/callback?code=xxx&state=yyy

1. Look up OAuthPendingFlow by state
2. flow.ProviderCode = "gemini"  →  load OAuthProviderConfig
3. flow.AiProviderCode = "gemini-acc1"  →  resolve target AiProvider
4. Exchange code → store token against AiProvider "gemini-acc1"
```

### Phase C — DB Schema

App uses `EnsureCreatedAsync()` — no EF migrations. New columns auto-added.

| # | Task | Detail | Regression Check |
|---|---|---|---|
| C1 | Schema auto-update on boot | EF mirrors entity properties | No manual SQL needed ✅ |
| C2 | Generate reference DDL (optional) | `dotnet ef dbcontext script` | Only if manual verify needed ✅ |

### Phase D — Create 7 Providers + Connect OAuth

**Manual via admin dashboard:**

| # | Task | Detail | Verification |
|---|---|---|---|
| D1 | Create 7 AiProvider rows | Code: `gemini-acc1`..`gemini-acc7`, Auth: OAuth, Config: gemini | `OAuthConfigId` set in DB |
| D2 | Run OAuth connect for each | Click "Connect" for each account → Google OAuth → callback | Each gets `ProviderOAuthToken` |
| D3 | Create 7 Models | Code: `gemini-2.0-flash-acc1`..`gemini-2.0-flash-acc7`, link to respective provider | `GET /v1/models` ✅ |
| D4 | Create API keys | Each key → allowed models subset (pins to specific Google account) | Test routing with curl |

**Google OAuth Client ID prerequisite:** `OAUTH_GEMINI_CLIENT_ID` must be set in `~/arkana-deploy/.env` on gateway-host.

### Phase E — Pipeline Verification (No Code Changes)

| Component | Current Behavior | For Multi-Account |
|---|---|---|
| `ChatEndpoints` (streaming) | Looks up model → gets `ProviderId` → decrypts `ApiKey` | OAuth provider skips ApiKey, uses OAuth token via `IOAuthTokenResolver` ✅ |
| `OpenAiCompatChatServiceBase.CompleteAsync` | `_catalog.GetByCodeAsync(code)` → if `UsesOAuth` → `_oauthResolver.GetBearerTokenAsync(provider.Id)` | Each provider has unique code, gets its own token ✅ |
| `SendChatHandler` | Model → Provider → FallbackChain → ChatService | Same path ✅ |
| `ApiKeyAuthMiddleware` | Validates key, stores `AllowedModelIds` | Key scoped to specific models ✅ |
| `OAuthTokenRefreshService` | `GetConnectedNeedingRefreshAsync()` → refreshes due tokens | Refreshes ALL 7 tokens ✅ |

### Phase F — UI Verification (No Code Changes)

| # | Task | Detail |
|---|---|---|
| F1 | OAuth connect button works per provider | `Providers.razor` uses provider code → POST `/oauth/{code}/start` |
| F2 | Status shows per-provider | `GET /oauth/{code}/status` returns correct status |

---

## File Change Summary

| File | Change | Risk |
|---|---|---|
| `Domain/Entities/ProviderOAuthToken.cs` | +1 property (`Label`), +1 factory param | Low |
| `Domain/Entities/OAuthPendingFlow.cs` | +1 property (`AiProviderCode`), +1 factory param | Low |
| `Domain/Interfaces/IOAuthRepositories.cs` | +1 method (`GetAllByProviderAsync`) | Low |
| `Infrastructure/Persistence/Repositories/OAuthRepositories.cs` | +1 method implementation | Low |
| `Infrastructure/OAuth/OAuthFlowService.cs` | Refactor `StartAsync`, `HandleCallbackAsync` | Medium (core logic) |
| Other files | No changes needed | ✅ |

**Total: 5 files changed, ~80 lines added.**

---

## Risk Mitigation

| Risk | Mitigation |
|---|---|
| Existing single-account OAuth breaks | Fallback chain: if no `AiProvider` by code, fall back to config lookup = original behavior. `AiProviderCode = null` = original flow. |
| Auto-refresh breaks | `GetConnectedNeedingRefreshAsync` returns ALL connected tokens — no filter change. |
| Token resolution breaks | `GetBearerTokenAsync(providerId)` uses `AiProviderId` directly — no behavior change. |
| Data loss on existing tokens | No schema drops data. `Label` nullable. `AiProviderCode` nullable. |
| Callback URL mismatch | Redirect URI uses config code (`gemini`) not provider code (`gemini-acc1`) — 1 URI in Google Cloud. |

---

## Verification Gate

| # | Test | Expected |
|---|---|---|
| V1 | Existing Gemini OAuth still works | Connect → callback → token stored for "gemini" provider |
| V2 | New `gemini-acc1` OAuth connect | Start flow → callback → token stored for "gemini-acc1" |
| V3 | Both tokens coexist | `GetAllByProviderAsync("gemini")` → 1 token. `GetAllByProviderAsync("gemini-acc1")` → 1 token |
| V4 | Token auto-refresh | `OAuthTokenRefreshService` refreshes all tokens |
| V5 | API key pinned to acc1 model | Request with key "Budi" → uses acc1's OAuth token only |
| V6 | No N8nService errors | Logs show 0 |
| V7 | No DEK errors | Logs show 0 |
| V8 | Unit tests pass | `dotnet test` — all green |

---

## Timeline

| Phase | Effort | Dependencies |
|---|---|---|
| A — Label + multi-token repo | 1h | None |
| B — Multi-account OAuth flow | 2h | Phase A |
| C — DB schema | 15m | Phase A |
| D — Create 7 providers + connect | 30m | Phase B + Google OAuth Client ID |
| E — Pipeline verification | 30m | Phase D |
| F — UI verification | 15m | Phase D |
| **Total** | **~4.5h** | |

**Non-code dependency:** Valid Google OAuth Client ID for `OAUTH_GEMINI_CLIENT_ID` env var.
