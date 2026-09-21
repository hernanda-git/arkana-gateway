# Google OAuth 2.0 (public) login — Gateway dashboard

> Status board: see **Progress** at the bottom. Each checkbox flips to `[x]` as the
> corresponding step is completed and verified. This document is the single source
> of truth for the feature; implementation follows it phase-by-phase (TDD first).

## Goal
Allow anyone with a valid Google account to log into the Blazor dashboard at
`https://gateway.arkana.dev` via Google OAuth 2.0 Authorization Code + PKCE.
Adds a **new** login path alongside (never replacing) the existing
ApiKey / Admin-Key / cookie flows. Google users land on a single, chrome-less
`/profile` page (no sidebar/topbar) with `User` role by default.

## Constants & decisions (resolved)
- **Public:** any Google account is accepted. `GoogleLogin__AllowedDomains` env stays
  available (default empty = open) so it can be locked later without a rebuild.
- **Role:** `GoogleLogin__DefaultRole=User`. `GoogleLogin__AdminEmails` (csv) maps specific
  addresses to `Admin`.
- **Callback:** ASP.NET Google scheme default `/signin-google` (proxy-friendly via
  the existing `UseForwardedHeaders`).
- **Configurable:** `GoogleLogin__ClientId` / `GoogleLogin__ClientSecret` live in
  `deploy/.env` (ASP.NET double-underscore nesting: `GoogleLogin__*`). Switching Google
  Cloud projects later = edit those two vars + `docker compose up -d gateway`. The
  redirect URI is identical across projects.
- **Auto-create:** first Google login creates a `DashboardUser` row scoped to the
  **default tenant** `00000000-0000-0000-0000-000000000001` (same as
  `AdminUserSeeder`). Cookie `NameIdentifier` = that row's Id (stable identity).

## Architecture map (verified against source)
- Cookie scheme: `CookieAuthenticationDefaults.AuthenticationScheme`
  (`Program.cs:60`). Google `SignInScheme` points here so the SAME cookie shape
  the dashboard expects is issued.
- Principal claims today (`Login.cshtml.cs:48`): `NameIdentifier, Name,
  ClaimTypes.Role, TenantId`. Google login must mint the same four.
- Page gating: `RoleAuthorizationMiddleware.IsProtectedPage` +
  `AdminAuthMiddleware`. Both read `ClaimTypes.Role` → Google users integrate.
- Default tenant constant: `AdminUserSeeder.cs:41`
  `Guid.Parse("00000000-0000-0000-0000-000000000001")`.
- Existing provider OAuth (`IOAuthFlowService`, `OAuthEndpoints`,
  `OAuthFlowService`) is for **upstream provider credentials** — deliberately
  NOT reused for portal login.

## Design (Phase 1)
1. `GoogleLoginOptions` (section `"GoogleLogin"`): `Enabled, ClientId,
   ClientSecret, AllowedDomains (csv, default ""), DefaultRole="User",
   AdminEmails (csv)`. Default-tenant id constant here too.
2. Register `AddGoogle` conditionally in `Program.cs` (after cookie builder,
   before `app.Build()`): `CallbackPath="/signin-google"`,
   `RequireProofKeyForCodeExchange=true`, `SaveTokens=false`,
   `Events = new GoogleLoginEvents(...)` (DI: `IDashboardUserRepository`,
   `GatewayDbContext`, `IOptions<GoogleLoginOptions>`, `ILogger`).
3. `GoogleLoginEvents.OnCreatingTicket`:
   - Read `email` + `email_verified` from the Google principal.
   - Domain allowlist: if `AllowedDomains` non-empty && domain not in it →
     `context.Fail("Google account domain not allowed")`.
   - Auto-create/find `DashboardUser` by `Email`; new rows get `TenantId =
     defaultTenantId`, `Role = AdminEmails.Contains(email) ? "Admin" : DefaultRole`.
   - Mint principal claims: `NameIdentifier=user.Id`, `Name=email`,
     `ClaimTypes.Role=role`, `TenantId=defaultTenantId`,
     `LoginProvider="Google"` (scope guard reads this).
4. `GoogleUserScopeMiddleware` (after `UseAuthentication`, before
   `RoleAuthorizationMiddleware`): if `LoginProvider=="Google"` AND the user is
   **not** in the `Admin` role, and path ∉ {`/profile`,`/logout`} → 302 to
   `/profile`. Enforces single-page UI for normal Google users. An **elevated
   (Admin) Google user is exempt** and keeps full dashboard access — the NavMenu's
   `<AuthorizeView Roles="Admin">` already gates admin-only links for them.
5. Login trigger: `app.MapGet("/auth/google/login", …).AllowAnonymous()` →
   `ChallengeAsync("Google", new AuthenticationProperties{RedirectUri="/profile"})`.
6. `UseWhen` ApiKeyAuthMiddleware predicate (`Program.cs:166`) extended to also
   exempt `/signin-google`, `/auth/google/login`, `/profile`.
7. UI: new `ChromeLessLayout.razor` (bare shell) + `Profile.razor`
   (`@page "/profile"`, `@layout ChromeLessLayout`, `@attribute [Authorize]`)
   showing name/email/role + logout.

## Google Cloud OAuth app (operator, not in repo)
- Type **Web application**; Authorized redirect URI
  `https://gateway.arkana.dev/signin-google`; JS origin
  `https://gateway.arkana.dev`; scopes `openid email profile`.
- Values → `deploy/.env` (`GoogleLogin__ClientId`, `GoogleLogin__ClientSecret`).

## Tests (Phase 2 — written BEFORE implementation)
File: `tests/Arkana.Gateway.Api.Tests/GoogleLogin/GoogleLoginTests.cs`
1. Happy public: Google ticket `anyone@gmail.com` → cookie set, role `User`,
   `TenantId==defaultTenantId`, `GET /profile` 200, `GET /` → 302 `/profile`.
2. Auto-create: no DB row pre-test → row exists post-login; 2nd login reuses Id.
3. Open by default: `AllowedDomains=""` accepts any Google email.
4. Domain deny (configured): `AllowedDomains=arkana.dev` + `x@other.com` → no
   cookie, redirect `/login?error=domain`.
5. Admin mapping: email in `GoogleLogin__AdminEmails` → `Role=="Admin"`, `GET /settings` 200.
6. PKCE/state tamper: altered `state`/`code_verifier` on `/signin-google` → 400, no cookie.
7. Scope guard: Google `User` hitting `/dashboard` → 302 `/profile`; `/logout` allowed.
8. API-key path unaffected: `POST /v1/chat/completions` no key → 401; with key → 200.
9. Admin API unaffected: `GET /admin/keys` `X-Admin-Key` → 200; anon → 401.
10. Provider OAuth untouched: `GET /oauth/configs` works; login created no
    `AiProvider`/token row.
11. Disabled toggle: `GoogleLogin:Enabled=false` → no button, `/auth/google/login`
    & `/signin-google` 404.
12. Logout: Google session `GET /logout` → cookie cleared, `GET /profile` → `/login`.

> Note: simulating the Google callback in TestHost requires either a fake
> OpenID/Token endpoint or invoking the handler with a pre-built `AuthenticationTicket`.
> The tests construct a `ClaimsPrincipal` as Google would return (nameidentifier +
> email + email_verified) and exercise `GoogleLoginEvents` directly + the
> `/profile` scope guard + regression paths. The end-to-end redirect/code-exchange
> is covered live in Phase 4.

## Implementation order (Phase 3)
1. `GoogleLoginOptions.cs` (+ default-tenant constant).
2. `GoogleLoginEvents.cs`.
3. `GoogleUserScopeMiddleware.cs`.
4. `Program.cs` wiring (AddGoogle, exempt paths, `/auth/google/login`, scope mw).
5. `ChromeLessLayout.razor` + `Profile.razor`.
6. `Login.cshtml` + `.cshtml.cs` (Google button + `?error=domain`).
7. `docker-compose.yml` + `.env.example` env vars.
8. `GoogleLoginTests.cs` (Phase 2 above).
9. Ship: SCP **only** edited files to `~/AI/Gateway` on gateway-host (do NOT clobber
   other in-flight work), then `docker compose build gateway && docker compose up -d gateway`.
   - Co-fix (out of scope bug): compose `ARKANA_ADMIN_PASSWORD` vs
     `.env.example` `ARKANA_ADMIN_USER_PASSWORD` mismatch in same env PR.

## Verify (Phase 4)
- `dotnet build` + `dotnet test` green (all four projects; the 12 Google tests).
- Deploy; live on `https://gateway.arkana.dev`:
  - Regression: Google disabled → password admin login, `/v1` 401, `/admin/keys`
    needs `X-Admin-Key` unchanged.
  - Google enabled → real login → chrome-less `/profile`; cookie + `User` role;
    `/logout` works; 2nd login reuses account (no dup row).
  - (If AllowedDomains set) out-of-domain bounce.
- Report only real curl codes / screenshots. No fabricated output.

## Progress
- [x] Write plan doc + checklist (this file)
- [x] P2 `GoogleLoginTests.cs` (14 tests, all GREEN)
- [x] P3 `GoogleLoginOptions.cs`
- [x] P3 `GoogleLoginEvents.cs`
- [x] P3 `GoogleUserScopeMiddleware.cs` (+ `GoogleClaims.IsGoogleLoginUser`) — Admin Google users exempt (full dashboard)
- [x] P3 `Program.cs` wiring (AddGoogle + exemptions + trigger + scope mw)
- [x] P3 Admin CRUD: Settings "User Management" section = Make Admin / Remove Admin (reuses AuthSvc.UpdateRoleAsync; /admin/users already had Change Role)
- [x] P3 fix: GoogleLoginEvents no longer overwrites stored role on re-login (env GoogleLogin__AdminEmails only seeds FIRST login; in-UI CRUD is authoritative)
- [x] P3 `Profile.razor`
- [x] P3 `Login.cshtml` + `.cshtml.cs`
- [x] P3 `docker-compose.yml` + `.env.example`
- [x] P4 `dotnet build` green (no warnings/errors)
- [x] P4 `dotnet test` green (253 Gateway.Api incl. 17 Google; 860+ total, 0 fail)
- [x] P4 Deploy to gateway-host + live verify (DONE — see Deployment notes)

> **Deployment notes (real, from this rollout):**
> - Env var naming MUST be `GoogleLogin__*` (double-underscore nesting) so ASP.NET
>   binds them into the `GoogleLogin` config section. Flat `GOOGLE_*` names DO NOT
>   bind → `GoogleLogin:Enabled` stays false → `/auth/google/login` returns 404.
> - `GoogleLogin__Enabled` must be the boolean literal `true`, NOT `1`
>   (`ConfigurationBinder` rejects `"1"` for `bool`; `AUTH=1` only works because it
>   is read via the lenient `GetValue<bool>`).
> - `deploy/.env` is the single source of ALL stack secrets. NEVER `mv`/overwrite it
>   with a partial local copy. To patch it, edit in place (append/replace only the
>   target lines). If it is lost, recover real values from the running containers'
>   env (`docker inspect <c> --format '{{range .Config.Env}}{{.}}{{"\n"}}{{end}}'`)
>   — for n8n specifically, the authoritative `N8N_ENCRYPTION_KEY` lives in the
>   container's `/home/node/.n8n/config` (`encryptionKey`), NOT necessarily in the
>   old `.env`. A mismatched key puts n8n into a crash loop.
> - Recreate only the target service (`docker compose up -d gateway`), not the whole
>   stack, to avoid needlessly restarting n8n/postgres.
> - Verified live (2026-08-19): `/login` 200 + Google button; `/auth/google/login`
>   302 → `accounts.google.com/.../v2/auth?client_id=...&scope=openid+profile+email
>   &response_type=code&code_challenge=...&code_challenge_method=S256&redirect_uri=...`;
>   `/v1` 401; `/admin/keys` 401; `/profile` 302. Gateway `Up (healthy)`, n8n `Up`,
>   restarts=0.
> - **Client migration (2026-08-19):** moved both the dashboard login AND the
>   Gemini provider OAuth to the same Google Cloud client `arkana-gateway-oauth`
>   (client id `999986130142-1ni9ctho9urb3seho0tjlkivv4jlfu1j.apps.googleusercontent.com`).
>   Wired via `GoogleLogin__ClientId`/`GoogleLogin__ClientSecret` AND
>   `OAUTH_GEMINI_CLIENT_ID`/`OAUTH_GEMINI_CLIENT_SECRET` (same id/secret). The
>   Gemini provider callback is `https://gateway.arkana.dev/oauth/gemini/callback`
>   (built in `OAuthFlowService.cs` as `{base}/oauth/{code}/callback`) — this URI
>   MUST be added as an authorized redirect URI on the `arkana-gateway-oauth` client in
>   Google Cloud, or Gemini "Connect" fails with a redirect_uri mismatch. The
>   dashboard login callback `…/signin-google` is already authorized.

> **.NET 10 API notes (discovered, not in original plan):** the Google auth
> package no longer exposes `GoogleEvents`/`GoogleCreatingTicketContext`. The
> correct types are `GoogleOptions` (with `Events` of type `OAuthEvents`),
> `options.UsePkce = true` (not `RequireProofKeyForCodeExchange`), and
> `OAuthCreatingTicketContext` whose `OnCreatingTicket` handler rejects via
> `context.Fail(string)` and replaces the principal by setting
> `context.Principal`. Events are wired through `RequestServices.GetRequiredService<GoogleLoginEvents>()`
> so DI (repo/db/logger) is resolved lazily per-request — avoids the
> `BuildServiceProvider` duplicate-singleton warning.
