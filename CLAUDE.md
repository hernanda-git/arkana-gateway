# CLAUDE.md

This file provides guidance to AI assistants (Claude Code, Cursor, etc.) when working with the ARKANA GATEWAY.

## Build & Run

```bash
# Build all projects
dotnet build

# Run all tests (xUnit). Counts drift — generate fresh:
#   dotnet test Arkana.slnx --list-tests 2>/dev/null | grep -c "\[Fact\]\|\[Theory\]"
dotnet test

# Run a single test project
dotnet test tests/Arkana.Domain.Tests          # 205 tests
dotnet test tests/Arkana.Application.Tests     # 78 tests
dotnet test tests/Arkana.Infrastructure.Tests  # 472 tests
dotnet test tests/Arkana.Gateway.Api.Tests     # 406 tests
dotnet test tests/Arkana.ServiceDefaults.Tests # 8 tests

# Run a single test class
dotnet test --filter "FullyQualifiedName~AiProviderTests"

# Run with code coverage
dotnet test --settings coverlet.runsettings

# Run the gateway (Docker infra + app)
./run.sh                                    # Linux/macOS/WSL
.\run.ps1                                   # Windows PowerShell
docker compose -f deploy/docker-compose.yml up -d  # Infra only
dotnet run --project src/Arkana.Gateway.Api      # App only

# Watch mode (hot reload)
ASPNETCORE_ENVIRONMENT=Development dotnet watch run --project src/Arkana.Gateway.Api
```

## EF Core Migrations

```bash
# Create migration
dotnet ef migrations add <Name> \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api

# Apply migrations
dotnet ef database update \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api

# Rollback last migration
dotnet ef database update <PreviousMigrationName> \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api

# Remove last (uncommitted) migration
dotnet ef migrations remove \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api
```

## Architecture

- **Clean Architecture**: Domain ← Application ← Infrastructure ← Gateway.Api
- **Patterns**: CQRS (MediatR), Repository, Options, Middleware Pipeline
- **Testing**: xUnit + NSubstitute + FluentAssertions + ASP.NET TestHost
- **Auth (middleware pipeline, applied in `Program.cs`)**:
  - `ApiKeyAuthMiddleware` — validates the tenant API key (`X-Api-Key` /
    `Authorization: Bearer`) for the `/v1` and `/mcp` surfaces, and defers
    `/admin` to the next stage.
  - `AdminAuthMiddleware` (added 2026-08-06) — authenticates the `/admin`
    management API. Accepts either a signed-in dashboard **Admin** session
    (cookie) or an `X-Admin-Key` / `Authorization: Bearer` matching
    `ADMIN_API_KEY`. Fails closed if no key is configured. Toggle
    `ADMIN_AUTH_ENABLED` (default on). **Previously `/admin` was entirely
    unauthenticated** — that was SEC-ARKANA-004, fixed in the 2026-08-06 session.
    A follow-up bypass (SEC-ARKANA-006, fixed 2026-08-07) let any GET carrying
    `Accept: text/html` skip the check entirely; the browser-page exemption is
    now matched against the fixed set of admin `@page` routes
    (`/admin/users`, `/admin/policy-templates`) rather than the caller-supplied
    header.
  - `RoleAuthorizationMiddleware` — gates the Blazor dashboard *pages*
    (`/settings`, `/admin/users`) when `Auth:Enabled` is true; the API surface
    is handled by the middleware above. Its `IsProtectedPage` list covers only
    `/admin/users` under the `/admin` prefix — it is **not** a fallback gate for
    the rest of the management API.
  - `RateLimitMiddleware`, `TokenTrackingMiddleware` — metering/limiting.
  - Dashboard login uses ASP.NET Core Cookie Authentication (`Auth:Enabled`,
    env `AUTH=1`).

  NOTE (2026-08-06): an earlier version of this file claimed a
  "ApiKeyAuth → TenantResolution → BudgetEnforcement" 3-layer pipeline. Neither
  `TenantResolution` nor `BudgetEnforcement` middleware ever existed in the
  codebase — that description was inaccurate. Tenant isolation is enforced in
  the application layer (budget reservation in `SendChatHandler`), not a
  middleware.
- **CORS**: `Program.cs` registers `AllowAnyOrigin().AllowAnyMethod()
  .AllowAnyHeader()` with **no `AllowCredentials`** (so no cookie/HTTP-basic
  credential cross-origin leakage — API auth is header-based, not cookies). Any
  web origin may call the gateway, which is acceptable for an internal gateway
  but should be tightened to an allow-list if it is ever exposed publicly.
  (Reviewed 2026-08-07; intentional, not a defect.)
- **CI**: there is **no CI pipeline** in this repository (no `.github/workflows`
  and no other CI config). The "70% code coverage gate" line below is
  aspirational — it is NOT enforced anywhere. Tests are run locally via
  `dotnet test`. If a gate is wanted, add a workflow that runs `dotnet test`
  with `coverlet.runsettings` (already present). (Corrected 2026-08-07.)
- **CI enforced**: 70% code coverage gate (NOT currently enforced — see CORS note above)

## Key Conventions

- Entities: private EF Core constructor + static `Create(...)` factory method
- Middleware: `UseWhen` predicates to exempt Blazor dashboard paths from API auth
- Endpoints: Minimal API extension methods in `Endpoints/` folder
- Tests: Use the same patterns (NSubstitute mocks, factory methods, InMemory EF)
- Commits: Conventional commit style (`feat:` / `fix:` / `docs:` / `chore:`)

## Key Endpoints (Maintenance)

- `POST /v1/chat/completions` — Chat completion (streaming + non-streaming)
- `POST /v1/images/generations` — Image generation
- `POST /v1/embeddings` — Text embeddings
- `POST /mcp/v1` — MCP JSON-RPC 2.0 endpoint
- `GET /admin/keys` — List API keys **(requires ADMIN_AUTH)**
- `GET /admin/providers` — List AI providers **(requires ADMIN_AUTH)**
- `POST /admin/key-pools` — Create key pool **(requires ADMIN_AUTH)**
- `POST /admin/templates` — Create compliance template **(requires ADMIN_AUTH)**
- `GET /admin/agents` — List agent definitions **(requires ADMIN_AUTH)**
- `POST /admin/webhooks` — Create webhook **(requires ADMIN_AUTH)**

## Important Files

| File | Purpose |
|------|---------|
| `src/Arkana.Gateway.Api/Program.cs` | DI registration, middleware pipeline, startup |
| `src/Arkana.Infrastructure/Persistence/GatewayDbContext.cs` | EF Core DbContext + entity configuration |
| `deploy/docker-compose.yml` | Full stack orchestration (PG, Redis, Qdrant, n8n) |
| `deploy/.env` | Secrets (not committed) |
| `PARITY-ROADMAP.md` | Strategic roadmap (6 phases, all complete) |
| `docs/` | Full project documentation, ADRs, architecture |
| `docs/OPERATIONS.md` | Runbooks: release/rollback, compose-edit verification, broker slots, credential recovery |
| `deploy/README-GATEWAY.md` | Install, configuration, troubleshooting (first run needs three `.env` values) |
| `deploy/gemini-broker/` | Gemini subscription broker slots — one container per Google identity |

## Test Counts (regenerate — do not hand-maintain)

- Domain: 205 | Application: 78 | Infrastructure: 472 | Gateway.Api: 406 | ServiceDefaults: 8 (2026-09-21, total 1169)
- **The total is the merge floor for `main`** — a release that lowers it has dropped coverage.

> Counts are a snapshot; regenerate instead of trusting this section:
> `dotnet test Arkana.slnx --no-build 2>/dev/null | grep -E "Passed!|Failed!"`

## Git Workflow — MANDATORY for AI agents

Read **`AGENTS.md`** (repo root) before any work. Summary:

- `main` is the ONLY long-lived branch; every task = new worktree + branch
  `<type>/<topic>-<UTCdate>` off fresh `origin/main`.
- The main clone's dirty WIP is protected — never stage/reset another lane's files.
- Merge protocol: fetch → rebase onto main → Release build 0/0 + full suite
  green → fast-forward push → delete your branch + worktree.
- Parallel/multi-session rules and conflict policy: `docs/multi-session-parallel-work.md`.
