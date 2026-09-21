# ARKANA GATEWAY — Deployment & Key Management Guide

## Architecture Overview

```
OpenCode CLI / Codex CLI / Any OpenAI-compat client
         │
         ▼  POST /v1/chat/completions
┌─────────────────────────────────────┐
│   ARKANA GATEWAY (localhost:5011)   │
│  .NET 10 + PostgreSQL + Redis + n8n │
└──────────┬──────────────────────────┘
           │
           ▼  POST chat/completions
┌──────────────────────┐
│  opencode.ai/zen/go  │  ← upstream provider
│  (or DeepSeek/Groq/  │
│   OpenRouter/... )   │
└──────────────────────┘
```

## Docker Stack

| Container | Service | Port | Purpose |
|-----------|---------|------|---------|
| `arkana-postgres` | PostgreSQL | 5432 | Provider config, models, usage logs |
| `arkana-redis` | Redis | 6379 | Caching, rate limiting state |
| `arkana-n8n` | n8n | 5678 | Workflow automation engine |
| `arkana-gateway` | Gateway API | 5011 | Main AI proxy + Blazor dashboard |

### Start / Stop

```bash
cd deploy/
docker compose up -d              # start all
docker compose up -d gateway      # start/recreate gateway only
docker compose up -d --force-recreate gateway  # force restart
docker compose down               # stop all (preserves volumes)
```

## API Gateway Access

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/v1/models` | List all registered models |
| `POST` | `/v1/chat/completions` | Chat completion (stream + non-stream) |
| `POST` | `/v1/responses` | Responses API (Codex CLI compat) |
| `POST` | `/v1/embeddings` | Generate embeddings (AI-ARKANA-006) |
| `GET` | `/` | Blazor Dashboard |
| `PUT` | `/admin/providers/{id}/apikey` | Update provider API key |

### Gateway API Keys

Two built-in bypass keys (configured in `appsettings.json` seed):

| Key | Purpose |
|-----|---------|
| `YOUR_GATEWAY_API_KEY` | Main admin key |
| `YOUR_GATEWAY_API_KEY` | Secondary bypass key |

These authenticate **to the gateway**, not to the upstream.

## Key Management

### Understanding the Credential Vault

The gateway uses **envelope encryption** for all upstream API keys:

```
Plaintext key  →  ICredentialVault.Seal()  →  Sealed "v1:..."  →  DB storage
Sealed "v1:..."  →  ICredentialVault.Open()  →  Plaintext key  →  HTTP call
```

The master key is auto-generated at startup and stored at:
- **Container**: `/app/Arkana/master.key`
- **Host**: `/root/.arkana/master.key` (if volume-mounted)

**⚠️ Critical**: If the container is rebuilt without persisting the master key, ALL sealed keys become undecryptable (`"Failed to unwrap DEK"`). This happens because each container instance generates a new master key.

### Flush & Re-seal Procedure

When master key is lost or rotated:

```bash
# 1. Flush all stale sealed keys
docker exec arkana-postgres sh -c \
  "PGPASSWORD=*** psql -U arkana -d arkana -c \"UPDATE \\\"AiProviders\\\" SET \\\"ApiKey\\\" = NULL\""

# 2. Restart gateway to clear in-memory cache
docker compose up -d --force-recreate gateway

# 3. Re-seal keys via admin API
curl -X PUT http://localhost:5011/admin/providers/{provider-id}/apikey \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: YOUR_GATEWAY_API_KEY" \
  -d '{"apiKey":"sk-your-actual-key-here"}'
```

### Provider IDs

| Provider | ID |
|----------|----|
| OpenCode | `a1000000-0000-0000-0000-000000000001` |
| OpenAI | `a1000000-0000-0000-0000-000000000002` |
| Gemini | `a1000000-0000-0000-0000-000000000003` |
| Anthropic | `a1000000-0000-0000-0000-000000000004` |
| Ollama | `a1000000-0000-0000-0000-000000000005` |
| DeepSeek | `a1000000-0000-0000-0000-000000000006` |
| MiniMax | `79ca97f8-37a6-4958-9067-e044584ff466` |

### API Key Storage Priority

Each `IChatCompletionService` resolves its API key in this order:
1. **DB** — `AiProviders.ApiKey` (sealed with envelope encryption)
2. **Env var** — `OPENCODE_GO_API_KEY` (for OpenCode provider only)
3. **Hardcoded default** — empty string (will fail with 401)

The `.env` file at `deploy/.env` sets `OPENCODE_GO_API_KEY` which is loaded
into the container as `ProviderOptions__OpenCode__ApiKey`.

## Upstream Connectivity

The gateway routes requests through `opencode.ai/zen/go/v1` by default.
This requires a **valid OpenCode Go API key** for chat completions.

| Endpoint | Auth Required | Status |
|----------|---------------|--------|
| `GET /v1/models` | No | ✅ Public |
| `POST /v1/chat/completions` | Yes | ❌ Key must be valid |

### Verify Key

```bash
# Test key directly against upstream
curl -s -X POST "https://opencode.ai/zen/go/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_KEY" \
  -d '{"model":"minimax-m3","messages":[{"role":"user","content":"hi"}],"max_tokens":5}'
```

Expected success:
```json
{"id":"chatcmpl-...","choices":[{"message":{"content":"Hello!"}}]}
```

## DB Schema (providers)

```sql
-- Provider config + sealed API key
SELECT "Code", "Name", "Priority", "IsEnabled",
       LENGTH("ApiKey") AS key_len,
       "BaseUrl"
FROM "AiProviders"
ORDER BY "Priority";

-- Models per provider
SELECT m."Code" AS model, p."Code" AS provider
FROM "Models" m
JOIN "AiProviders" p ON m."ProviderId" = p."Id";
```

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Failed to unwrap DEK` | Master key changed; keys sealed with old key | Flush + re-seal (see above) |
| `Invalid API key` (upstream 401) | OpenCode API key expired | Get new key from opencode.ai |
| `HTTP 404` on gateway | Container not healthy yet | Wait 10s for health check |
| `No service for type 'SsrfSafeHttpHandler'` | DI missing transient registration | Already fixed in `dev` |
| `No service for type 'IDialectTranslator'` | Interface not registered | Already fixed in `dev` |

## Phase 3 Feature Status (dev branch)

| Task | Feature | Status |
|------|---------|--------|
| #14 | Canonical request model + dialects | ✅ Merged |
| #15a/b | Anthropic connector | ✅ Merged |
| #16 | 5 new providers (Groq, OpenRouter, Qwen, GLM, Cloudflare) | ✅ Merged |
| #18 | `/v1/embeddings` endpoint | ✅ Merged |
| #19 | `Console.Error.WriteLine` cleanup | ✅ Merged |
| #17 | Semantic caching | ⏳ Pending |
| #20 | Custom OpenTelemetry metrics | ⏳ Pending |
| #15d | Gemini dialect | ⏳ Pending |

## Commits on dev (this session)

```
9de7caf fix(deepseek): resolve BaseAddress for per-request URL construction
a418c37 fix(di): register IDialectTranslator interface for OpenAi dialect
d6faceb fix(di): register SsrfSafeHttpHandler transient (SEC-ARKANA-004)
c350c24 feat(embeddings): add /v1/embeddings endpoint (AI-ARKANA-006, task 18)
61e8487 fix(telemetry): replace Console.Error.WriteLine with ILogger (task 19)
```
