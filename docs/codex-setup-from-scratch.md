# Codex ↔ ARKANA GATEWAY — From-Scratch Setup

> **Canonical production client guide:** [`codex-gateway-installation.md`](codex-gateway-installation.md). This older document remains as the Gateway-from-zero engineering walkthrough. For employee setup use the canonical guide and `scripts/setup-codex-gateway.ps1`.

> **Goal:** stand up the ARKANA GATEWAY from zero and connect **Codex** (OpenAI's
> coding agent) to it so Codex runs its full multi-turn tool loop through the
> company's `deepseek-v4-flash` model.
>
> **Last verified:** 2026-07-21 against live `gateway.arkana.dev` (DigiCert HTTPS).
> **Repo:** `github.com/AI/gateway` (local working copy `C:\\Workspace\\gateway`).
>
> ### 🚀 Adapter retired 2026-07-21
> The local `arkana_gw_adapter.js` (`:8892`) has been **retired and archived**.
> `gateway.arkana.dev` has a **DigiCert commercial cert** (`*.arkana.dev`,
> GeoTrust TLS RSA CA G1, valid Sep 2025 → Sep 2026) — system-trusted, no
> `-k`, no `NODE_EXTRA_CA_CERTS`, no adapter needed.
> Codex now points **directly** at `https://gateway.arkana.dev/v1`.

---

## 0. TL;DR

The gateway **natively speaks the OpenAI Responses API** at `POST /v1/responses`.
Codex talks *only* the Responses API, so the integration is just:

1. Run the gateway (Docker stack or `dotnet run`).
2. Create an API key in the dashboard.
3. Point Codex's `model_provider` at `http://localhost:5011/v1`.

**No client-side adapter, proxy, or cert import is required for local/dev or LAN/remote.**
The earlier `arkana_gw_adapter.js` localhost shim was **retired 2026-07-21** — the gateway's
DigiCert `*.arkana.dev` cert is system-trusted, so any client can connect directly:

---

## 1. Architecture — how Codex reaches the model

```
┌──────────────┐   HTTPS POST /v1/responses   ┌──────────────────────────┐
│   Codex CLI  │ ───────────────────────────▶ │  ARKANA GATEWAY :5011    │
│  (Windows/   │ ◀─────────────────────────── │  /v1/responses endpoint  │
│   Linux/Mac) │   SSE: response.created →    │  (ResponsesEndpoints.cs) │
│              │   output_text.delta →        │                          │
│              │   function_call →            │  translates Responses →  │
│              │   response.completed          │  Chat Completions →      │
└──────────────┘                              └──────────┬───────────────┘
                                                        │ HTTPS
                                                        ▼
                                              ┌──────────────────────┐
                                              │ OpenCode "Console Go" │
                                              │ opencode.ai/zen/go/v1 │
                                              │ → deepseek-v4-flash   │
                                              └──────────────────────┘
```

`ResponsesEndpoints.cs` does the heavy lifting (verified in source):

- Parses the Responses request (`input`, `instructions`, `tools`, `tool_choice`, `stream`).
- Flattens the **multi-turn tool loop** into a valid Chat-Completions `messages[]`
  array — including bare `{role,content}` user items, **standalone `function_call`**
  items, and **parallel** tool calls collapsed into one assistant `tool_calls`
  message (the 2026-07-19 fixes that stopped the `Reconnecting 5/5` error).
- Maps unknown model names (e.g. Codex-internal `gpt-5.6-luna`) to
  `deepseek-v4-flash` so the upstream never 401s mid-turn.
- Emits the **exact Responses SSE event stream** Codex expects:
  `response.created` → `response.in_progress` → `response.output_item.added`
  → `response.output_text.delta` / `response.function_call_arguments.delta`
  → `response.*.done` → `response.completed`, plus 10-second `: heartbeat`
  comments to keep strict clients alive during long reasoning delays.
- Retries once with the fallback model if the upstream reports "model not supported".

---

## 2. Prerequisites

| Tool | Version | Check | Install |
|------|---------|-------|---------|
| Git | 2.40+ | `git --version` | package manager |
| .NET SDK | 10.0+ | `dotnet --version` | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Docker (+ Compose v2) | 24+ | `docker --version` / `docker compose version` | Docker Desktop / `docker-ce` |
| Node.js *(Codex only)* | 18+ | `node --version` | https://nodejs.org |
| Codex CLI | latest | `codex --version` | `npm i -g @openai/codex` |

> Windows users: WSL2 gives the smoothest experience (path: `/mnt/c/Workspace/gateway`).
> Native PowerShell works too — all commands below are POSIX; translate `export` → `$env:` /
> drop the leading `./` as needed.

---

## 3. Clone

```bash
cd ~
git clone https://github.com/hernanda-git/arkana-gateway.git
cd gateway
```

---

## 4. Configure environment

The `deploy/.env` file supplies secrets to the Docker stack. Copy the template and fill it in.

```bash
cp deploy/.env.example deploy/.env
```

Edit `deploy/.env`:

```dotenv
# Required — upstream OpenCode Go key (the model backend Codex uses)
OPENCODE_GO_API_KEY=sk-...

# Required — n8n encryption key (generate once, keep stable across restarts)
N8N_ENCRYPTION_KEY=$(openssl rand -hex 32)

# n8n owner account (used by the gateway for cookie-auth against n8n's API)
N8N_USER_EMAIL=admin@example.com
N8N_USER_PASSWORD=change-me
```

> `OPENCODE_GO_API_KEY` is the **only** provider key the gateway strictly needs for
> Codex, because the Responses endpoint resolves the model to the OpenCode/Console Go
> provider by default. Other providers (OpenAI, Anthropic, Gemini) are optional and
> configured later in the dashboard.

---

## 5. Start the stack

### Option A — Full container (gateway + infra in Docker) ⭐ recommended

```bash
docker compose -f deploy/docker-compose.yml up -d
```

Brings up: `arkana-postgres` (5432), `arkana-redis` (6379),
`arkana-qdrant` (16333/16334), `arkana-n8n` (5678), `arkana-gateway` (5011).

### Option B — Infra in Docker, gateway as a hot-reload .NET process (dev)

```bash
docker compose -f deploy/docker-compose.yml up -d postgres redis qdrant n8n
```

The gateway is then run with `dotnet run` (see §6) for fast iteration.

Verify everything is up:

```bash
docker ps --filter "name=arkana-*" --format "table {{.Names}}\t{{.Status}}"
```

All four core containers should show `Up` / `healthy`.

---

## 6. Apply database migrations

Required once (the EF Core schema). The setup wizard does this automatically;
manually:

```bash
dotnet ef database update \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api
```

> If `dotnet ef` is unavailable: `dotnet tool install --global dotnet-ef`
> and add `~/.dotnet/tools` to `PATH`.

On first run the gateway **auto-seeds**: default tenant, pricing plans, admin user
(`ARKANA_ADMIN_USER` / `ARKANA_ADMIN_PASSWORD`, default `admin`/`admin`),
compliance templates, and the built-in providers (including OpenCode/Console Go).

---

## 7. Launch the gateway (Option B only)

Skip this step if you used Option A (the container already runs the gateway).

```bash
dotnet run --project src/Arkana.Gateway.Api
# or, with hot reload:
ASPNETCORE_ENVIRONMENT=Development dotnet watch run --project src/Arkana.Gateway.Api
```

Confirm it's alive:

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5011/health   # -> 200
curl -s http://localhost:5011/v1/models | head -c 200                  # lists models
```

Open the dashboard: **http://localhost:5011/** (API docs at `/scalar/v1` in dev).

---

## 8. Create an API key

Codex authenticates with a gateway API key (`X-Api-Key` or `Authorization: Bearer`).

**Via the dashboard (GUI):**
1. Open http://localhost:5011/ → **API Keys** → **Create Key**.
2. Name it (e.g. `Codex Desktop`), leave *Allowed Models* empty for all models.
3. **Copy the key immediately** — shown only once.

**Or via the admin API (CLI):**

```bash
curl -s -X POST http://localhost:5011/admin/api-keys \
  -H "Content-Type: application/json" \
  -d '{"name":"Codex Desktop","allowedModelIds":[]}' | python3 -m json.tool
```

Save the `plainTextKey` to an environment variable:

```bash
export GATEWAY_API_KEY="YOUR_GATEWAY_API_KEY"
```

---

## 9. Configure Codex

Codex reads **`~/.codex/config.toml`** and speaks the **Responses API** natively.
Point its `model_provider` at the gateway.

### 9.1 Create / edit `~/.codex/config.toml`

```toml
# ~/.codex/config.toml
[model_providers.arkana-gateway]
name     = "ARKANA GATEWAY"
base_url = "http://localhost:5011/v1"   # Codex appends /responses
wire_api = "responses"                  # tells Codex to use the Responses API
experimental_bearer_token = "YOUR_GATEWAY_API_KEY"
# (alternatively: env_key = "GATEWAY_API_KEY" to read the key from that env var)

# Make it the default so `codex` just works
model_provider = "arkana-gateway"
model          = "deepseek-v4-flash"
```

> **Field names verified against Codex CLI 0.141.** The key is
> `experimental_bearer_token` (or `env_key` to pull from an environment variable),
> not `api_key`. `wire_api = "responses"` is required so the request lands on the
> gateway's `/v1/responses` endpoint. `base_url` must end in `/v1` (Codex appends
> `/responses`).

### 9.2 (Alternative) drive it from an environment variable

If you prefer not to edit the TOML, set the key via env:

```bash
export CODEX_API_KEY="YOUR_GATEWAY_API_KEY"
export CODEX_MODEL_PROVIDER="arkana-gateway"
export CODEX_BASE_URL="http://localhost:5011/v1"
```

Then launch: `codex --model-provider arkana-gateway`.

---

## 10. Verify — the real tool loop

A single `hello` proves nothing. The thing that *used* to break is the
**multi-turn tool loop** (Codex runs a command, gets the result, continues).
Exercise it explicitly:

```
codex
> what time is it
> make a folder C:\temp\codextest and write hello.txt into it
```

If Codex runs the commands and returns an answer (no `Reconnecting 5/5`), you're good.

Programmatic health checks:

```bash
# 1) Gateway reachable
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5011/health   # 200

# 2) Responses endpoint answers a simple prompt
curl -s -N -X POST http://localhost:5011/v1/responses \
  -H "Authorization: Bearer $GATEWAY_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4-flash","input":"say hi in one word","stream":true}' \
  | grep -m1 'response.completed' && echo "RESPONSES_OK"
```

---

## 11. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `401` from `/v1/responses` | Missing/wrong API key | Re-create key (§8); ensure `Authorization: Bearer` or `X-Api-Key` header. |
| Codex hits `/v1/chat/completions` (404/format error) | `wire_api` not set to `responses` | Add `wire_api = "responses"` to the `model_providers` block (§9.1). |
| `Reconnecting 5/5` **after tools ran** | Stale gateway image with the old translator | Redeploy the gateway after 2026-07-19 (`ResponsesEndpoints.cs`). Restart container / re-run `dotnet run`. |
| `Upstream returned 400 … Console Go` | Unknown model name not remapped | Confirm gateway is current; it remaps unknown models to `deepseek-v4-flash` automatically. |
| `connection refused` to `localhost:5011` | Gateway not running | `docker ps` / `dotnet run` (§5–7). |
| Codex points at `gateway.arkana.dev` → TLS error (outdated — cert is now DigiCert trusted) | Gateway cert was self-signed `CN=localhost` (no SAN) **before 2026-07-20** | The DigiCert `*.arkana.dev` cert is now installed and system-trusted. `https://gateway.arkana.dev/v1` works directly with full validation. |

---

## 12. LAN / remote Codex (adapter retired 🚫)

The `arkana_gw_adapter.js` (`:8892`) was **retired 2026-07-21**. It is no longer needed
because `gateway.arkana.dev` has a **DigiCert commercial cert** that is system-trusted.

Codex on a **different machine** across the LAN/VPN can use:

```
base_url = "https://gateway.arkana.dev/v1"
```

The DigiCert cert (`CN=*.arkana.dev`) is trusted by all major OSes, browsers, and
Node.js — no `NODE_EXTRA_CA_CERTS`, no `-k`, no adapter. The only requirement is DNS
resolution of `gateway.arkana.dev` → `10.10.0.10` (internal DNS or VPN).

> ⚠️ **Remote/public access** (outside the LAN) still requires a Cloudflare Tunnel or
> public IP — the cert covers HTTPS trust, not reachability. See
> `docs/pending-leaf-cert-gateway.md`.

---

## 13. One-command alternative: the Setup Wizard

For a fully interactive, zero-guess bootstrap (detects platform, installs missing
deps, creates the DB, applies migrations, generates an API key, runs the test suite):

```bash
./setup.sh            # Linux/macOS/WSL
# or:  .\setup.ps1    # Windows PowerShell
# or:  dotnet run --project tools/Arkana.Setup
```

The wizard writes `deploy/.env` and `appsettings.json` for you, then prints the
dashboard URL and a ready-to-use API key. After it finishes, jump to §9 to wire Codex.

---

## 14. Reference — Responses endpoint contract

| Item | Detail |
|------|--------|
| Route | `POST /v1/responses` (also `/v1/responses` under the `/v1` group) |
| Auth | `X-Api-Key` **or** `Authorization: Bearer <key>` |
| Request fields | `model` (default `deepseek-v4-flash`), `instructions`, `input` (string or item array), `tools`, `tool_choice`, `stream`, `max_output_tokens` |
| Streaming events | `response.created`, `response.in_progress`, `response.output_item.added`, `response.content_part.added`, `response.output_text.delta`, `response.function_call_arguments.delta`, `response.output_text.done`, `response.content_part.done`, `response.output_item.done`, `response.completed`, `response.failed` |
| Non-streaming | Single JSON `response` object with `output[]` (messages + `function_call`s) and `usage` |
| Model remap | Unknown model names → `deepseek-v4-flash` (avoids upstream 401) |
| Keep-alive | 10s `: heartbeat` SSE comments during silent upstream thinking |

*This document supersedes the Codex sections of `docs/agent-cli-integration-setup.md`
and reconciles `docs/codex-gateway-employee-guide.md`.*
