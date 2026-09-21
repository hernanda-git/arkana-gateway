# ARKANA GATEWAY — Deployment & Operations Guide

> **Platforms:** Linux (Ubuntu 22.04+), Windows 11 (Docker Desktop / WSL2), macOS 14+
> **Requirements:** Docker 24+ with the Compose plugin. Nothing else — the .NET 10 app builds inside the image.
> **First-run goal:** after `clone` + three commands the dashboard answers on `http://localhost:5011`.

---

## 1. Quick start (Docker)

```bash
git clone <your-remote>/gateway.git
cd gateway/deploy
cp .env.example .env          # fill in the values marked REQUIRED in that file
docker compose up -d --build  # the first build takes a few minutes
```

Wait for the healthcheck, then verify:

```bash
docker compose ps                                                        # gateway shows (healthy)
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5011/health     # → 200
```

Open the dashboard at **http://localhost:5011** and log in with
`ARKANA_ADMIN_USER` / `ARKANA_ADMIN_PASSWORD` from `deploy/.env`.

### The only values you must provide

| Variable | Why it is required |
|---|---|
| `N8N_ENCRYPTION_KEY` | The compose refuses to start without it (`openssl rand -hex 32`). |
| `ARKANA_ADMIN_USER` / `ARKANA_ADMIN_PASSWORD` | `AUTH=1` is hardcoded, and the gateway refuses to seed an admin user without a password. |
| `ARKANA_MASTER_KEY` *(recommended)* | Encrypts provider credentials at rest. When empty the gateway generates one into the `dataprotection` volume — fine for a single host, but then that volume holds your encryption key. |

Every other value in `.env.example` sits under an `OPTIONAL` heading and may stay empty:
the gateway starts and serves without them. `deploy/.env.example` documents each one,
including what breaks when it is missing.

**No upstream provider credential is needed to boot.** Add providers and API keys from
the dashboard (`/providers`, `/apikeys`) — they are stored encrypted — or set
`OPENROUTER_API_KEY` / `OPENCODE_GO_API_KEY` to seed the built-in provider rows.

---

## 2. What runs

| Service | Container | Port(s) | Purpose |
|---|---|---|---|
| **Gateway** | `arkana-gateway` | `5011` | API (`/v1/chat/completions`, `/v1/responses`) + Blazor dashboard |
| **Database baseline** | `arkana-db-baseline` | — | One-shot: initialises an empty database from `deploy/db/baseline-schema.sql` + `baseline-seed.sql` and stamps the migration history. No-ops when a history already exists. |
| **PostgreSQL 16** | `arkana-postgres` | `5432` | Config, request logs, metering, n8n schema |
| **Redis 7** | `arkana-redis` | `6379` | Cache, rate limiting, metering counters |
| **Qdrant** | `arkana-qdrant` | `16333` REST / `16334` gRPC | Semantic cache (host ports remapped to avoid collisions) |
| **n8n** | `arkana-n8n` | `5678` | Workflow engine (optional integration) |

All services share the bridge network `arkana-net`; the gateway reaches n8n at
`http://n8n:5678`. Postgres/Redis/Qdrant credentials are **development defaults
hardcoded in `docker-compose.yml`** — they are not `.env` values (see the closing
section of `.env.example`).

### Why a schema baseline instead of running migrations

The migration history in `src/Arkana.Infrastructure/Migrations` cannot rebuild the
production schema on an empty database: migrations were edited after they had been
applied, and at least one table (`OAuthPendingFlows`) has no creating migration left in
the repository. `dotnet ef database update` on a fresh database therefore aborts partway
and the gateway crash-loops.

`deploy/db/` closes that gap:

| File | Role |
|---|---|
| `baseline-schema.sql` | Schema dump of the running production database (schema only, public schema, no data, no owners/privileges, history table included but empty). |
| `baseline-seed.sql` | The reference rows the gateway cannot boot without (currently the default tenant). No production data, no credentials. |
| `baseline-migrations.txt` | The migration ids already applied to that schema; stamped into `__EFMigrationsHistory`. |
| `init-baseline.sh` | Applies the three above to an **empty** database. If a migration history already exists it exits immediately, so it runs harmlessly in every environment; if the database has tables but no history it refuses to touch it. |

Migrations written **after** this baseline apply normally on both lineages. When you add
one, the fresh-install path and the existing databases converge on the same schema.

---

## 3. Optional features

Each is independent; skip whatever you do not use.

| Feature | Where |
|---|---|
| **Providers, models, API keys, agents** | Dashboard: `/providers`, `/apikeys`, `/agents`. No `.env` needed. |
| **Google login for the dashboard** | `.env` → `GoogleLogin__*` (redirect URI `<host>/signin-google`). |
| **Gemini subscription (Antigravity) accounts** | Broker slots — see [`gemini-broker/README.md`](gemini-broker/README.md). One container per Google identity. |
| **ChatGPT/Codex account pool** | Dashboard `/providers` → *Add account* on the Codex provider. |
| **Tenant budgets** | `.env` → `BUDGET_ENFORCER_ENABLED=true`. |
| **Admin REST API for automation** | `.env` → `ADMIN_API_KEY` (fails closed when unset). |
| **Semantic cache** | On by default (`SemanticCache__Enabled=true`, backed by Qdrant). |

---

## 4. Verification & day-2 commands

```bash
# health + which image is actually running
curl -s http://localhost:5011/health
docker inspect -f '{{.Config.Image}} {{.State.Health.Status}}' arkana-gateway

# migrations applied
docker exec arkana-postgres psql -U arkana -d arkana -Atc \
  'SELECT count(*) FROM "__EFMigrationsHistory";'

# chat (non-stream) using a key created in /apikeys
curl -s http://localhost:5011/v1/chat/completions \
  -H "Authorization: Bearer <api-key>" -H 'Content-Type: application/json' \
  -d '{"model":"<model-code>","messages":[{"role":"user","content":"hi"}]}'

# streaming (expect `data: ...` chunks and a final `data: [DONE]`)
curl -sN http://localhost:5011/v1/chat/completions \
  -H "Authorization: Bearer <api-key>" -H 'Content-Type: application/json' \
  -d '{"model":"<model-code>","messages":[{"role":"user","content":"hi"}],"stream":true}'

# logs: real failures only
docker logs arkana-gateway 2>&1 | grep -E 'fail:|crit:'
```

Rebuild after pulling new code (migrations run automatically at startup):

```bash
docker compose up -d --build gateway
```

---

## 5. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `docker compose up` aborts: *"N8N_ENCRYPTION_KEY is not set"* | Copy `.env.example` → `.env` and generate one (`openssl rand -hex 32`). The compose message names the variable. |
| `docker compose up` aborts: *"ARKANA_ADMIN_PASSWORD is not set"* | Same file — the dashboard needs an admin password because `AUTH=1` is hardcoded. |
| Login rejected, or logs show *"ARKANA_ADMIN_PASSWORD is required when no admin user exists"* | `deploy/.env` still holds the empty/placeholder value, or the container was not recreated after editing it: `docker compose up -d --force-recreate gateway`. |
| `"Failed to unwrap DEK. The master key may have changed"` | The `dataprotection` volume (auto-generated master key) was removed while the database still holds encrypted provider keys. Restore the volume, or set `ARKANA_MASTER_KEY` in `.env` so the key is explicit and stable. |
| n8n endpoints return 401 in the dashboard | `N8N_EMAIL`/`N8N_PASSWORD` (n8n's own login) and `N8N_USER_EMAIL`/`N8N_USER_PASSWORD` (gateway → n8n) must match the running n8n instance; recreate the gateway after editing them. |
| n8n logs *"Postgres 16 is outside the supported range and receives compatibility support only. Upgrade to Postgres 17 or newer."* | Expected with the pinned `postgres:16-alpine` image — n8n runs in compatibility mode. Changing the database image is a migration, not a config tweak: do it deliberately, with a dump. |
| n8n logs *"Failed to start Python task runner in internal mode … Python 3 is missing"* | Expected: the n8n image ships no Python. Only nodes that execute Python code are affected; JavaScript nodes (the default) work. |
| n8n takes a minute on first start | It runs its own database migrations and prints `Recorded version change: (none) -> <version>`; the editor is reachable at `http://localhost:5678` once it prints `Editor is now accessible`. |
| Qdrant never becomes `(healthy)` | Known: the Qdrant image ships no `curl`/`wget`; the compose healthcheck uses a bash TCP probe instead. Do not "fix" it with a curl-based healthcheck. |
| Port already in use | Compose maps `5011`, `5432`, `6379`, `5678`, `16333`, `16334`. Change the host side of the mapping in `docker-compose.yml`. |
| Streaming returns `503 {"detail":"Gemini subscription streaming failed."}` while non-streaming works | `GeminiSubscription__ProviderId` is unset while broker slots are configured. The gateway logs exactly this misconfiguration at startup under the `GeminiBrokerConfig` category. Details: [`gemini-broker/README.md`](gemini-broker/README.md#troubleshooting). |
| Startup logs `Error: libgssapi_krb5.so.2: cannot open shared object file` | Benign: the .NET runtime notes a Kerberos/GSSAPI library the image intentionally does not ship (the runtime stage avoids `apt` so the image does not depend on mutable package indexes). Only needed for GSSAPI database authentication. |
| A database created from scratch fails with `42703: column "..." does not exist` while an existing database is fine | A migration is invisible to EF Core — it has neither the generated `.Designer.cs` nor inline `[Migration]`/`[DbContext]` attributes, so it is never applied to a new database. Detect: `dotnet ef migrations list` and compare with the files in `src/Arkana.Infrastructure/Migrations`. Fix: restore the attributes (and make the operation idempotent so databases that already recorded it stay happy). |
| The `db-baseline` container exits non-zero | It logs the reason. Empty database → baselined. Existing migration history → nothing to do (exit 0). Database with tables but no history → it refuses rather than half-initialise: inspect it, or drop and recreate the database. |
| Broker slot answers 400/403 on every call | The Google credential in that slot is incomplete — an interrupted sign-in saves a token without Antigravity onboarding. See the broker README's troubleshooting section. |
| Dashboard form buttons stay disabled while typing | Historical bug class (`@bind` commits on blur). Fixed across the dashboard; if you add a gated form, bind text inputs with `@bind:event="oninput"`. |

Deeper runbooks — releasing, rollback, adding broker slots, credential recovery —
live in [`../docs/OPERATIONS.md`](../docs/OPERATIONS.md).

---

## 6. Releasing to a server

Production and staging run the same compose file with different `.env` values and an
image-tag gate:

```bash
# build and tag the candidate, then promote the tag
docker build -t <registry>/gateway:candidate-<sha> .
docker tag  <registry>/gateway:candidate-<sha> <registry>/gateway:latest
docker compose up -d --no-build --no-deps gateway
```

Always keep the previous image tagged (`rollback-pre-<sha>-<timestamp>`): rolling back
is then a `docker tag` plus `up -d`. The full checklist — parity check against the
running release, digest approval, post-deploy verification, rollback recipe — is in
[`../docs/OPERATIONS.md`](../docs/OPERATIONS.md#release--rollback).

---

## 7. Log levels

The compose pins a few noisy categories to `Error` for readable logs
(`Logging__LogLevel__*`): `Microsoft.AspNetCore.Hosting` (Blazor auth challenges),
`Arkana.Infrastructure.Security` (credential-vault bootstrap), EF Core query
logging and MediatR. Application failures still log at `fail:`/`crit:` — see §4.
