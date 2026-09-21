# AI Gateway — Agent Provider Integration Guide

> **Production correction:** use [`codex-gateway-installation.md`](codex-gateway-installation.md) for Codex and [`agent-gateway-compatibility.md`](agent-gateway-compatibility.md) for Agent support status. Examples in this historical guide that use `http://localhost:5011` are local-development examples, not the production Endpoint. Production is `https://gateway.arkana.dev/v1`.

> **Use this guide to configure any AI coding agent / assistant to use ARKANA GATEWAY as its LLM provider.**

---

## 1. Overview

The **ARKANA GATEWAY** is a self-hosted, OpenAI-compatible API gateway that routes chat completion requests to multiple AI providers through a single endpoint. It supports:

- **OpenAI-compatible API** (`/v1/chat/completions`, `/v1/models`)
- **Streaming and non-streaming** responses
- **API key authentication** (managed via dashboard)
- **Usage tracking, rate limiting, cost monitoring**
- **n8n workflow integration** for automation

**Base URL:** `http://<host>:5011` (default: `http://localhost:5011`)

---

## 2. Authentication

Every request to the Gateway API requires an API key passed via the `Authorization` header:

```
Authorization: Bearer arkana-xxxxxxxxxxxx
```

### Getting an API Key

1. Open the Gateway Dashboard: `http://localhost:5011/`
2. Navigate to **API Keys** in the sidebar
3. Click **Create Key**, give it a name, and select which models it can access
4. **Copy the key immediately** — it's shown only once (the raw prefix is shown in the UI for identification)

> ⚠️ **Security:** API keys are stored as SHA-256 hashes in the database. Never commit raw keys to Git. Store them in environment variables or a local `auth.json`.

---

## 3. API Endpoints

### List Models

```http
GET /v1/models
Authorization: Bearer <api-key>
```

Returns available models configured in the Gateway. Each model maps to a provider backend (OpenCode, OpenAI, etc.).

### Chat Completion

```http
POST /v1/chat/completions
Authorization: Bearer <api-key>
Content-Type: application/json

{
  "model": "deepseek-v4-flash",
  "messages": [
    { "role": "system", "content": "You are a helpful assistant." },
    { "role": "user", "content": "Hello!" }
  ],
  "stream": false,
  "max_tokens": 4096,
  "temperature": 0.7
}
```

**Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `model` | string | required | Model code from `/v1/models` |
| `messages` | array | required | Chat messages array |
| `stream` | boolean | `false` | Enable SSE streaming |
| `max_tokens` | int | `4096` | Max tokens in response |
| `temperature` | float | `0.7` | Sampling temperature |

**Response (non-streaming):**

```json
{
  "id": "chatcmpl-xxx",
  "object": "chat.completion",
  "created": 1717000000,
  "model": "deepseek-v4-flash",
  "choices": [{
    "index": 0,
    "message": {
      "role": "assistant",
      "content": "Hello! How can I help you today?"
    },
    "finish_reason": "stop"
  }],
  "usage": {
    "prompt_tokens": 10,
    "completion_tokens": 20,
    "total_tokens": 30
  }
}
```

---

## 4. Agent Configuration Examples

### 4.1 OpenCode CLI

**Config file:** `~/.config/opencode/opencode.json`

```json
{
  "providers": {
    "arkana-gateway": {
      "name": "ARKANA GATEWAY",
      "apiKey": "<your-api-key>",
      "baseURL": "http://localhost:5011/v1",
      "models": {
        "default": ["deepseek-v4-flash"],
        "fetch": true
      }
    },
    "opencode-go": {
      "name": "OpenCode Go (via Gateway)",
      "apiKey": "<your-api-key>",
      "baseURL": "http://localhost:5011/v1",
      "models": {
        "default": ["deepseek-v4-flash"],
        "fetch": true
      }
    }
  }
}
```

**Auth file:** `~/.local/share/opencode/auth.json`

```json
{
  "providers": {
    "arkana-gateway": "<your-api-key>",
    "opencode-go": "<your-api-key>"
  }
}
```

### 4.2 Claude Code / Anthropic-based Agents

```bash
# Via environment variables (OpenAI-compatible mode)
export ANTHROPIC_BASE_URL="http://localhost:5011/v1"
export ANTHROPIC_API_KEY="<your-api-key>"

# Or for any OpenAI-compatible client
export OPENAI_API_KEY="<your-api-key>"
export OPENAI_BASE_URL="http://localhost:5011/v1"
```

### 4.3 Cline / VS Code Extension

In Cline settings:

```json
{
  "apiProvider": "openai",
  "openAiApiKey": "<your-api-key>",
  "openAiBaseUrl": "http://localhost:5011/v1",
  "openAiModel": "deepseek-v4-flash"
}
```

### 4.4 `curl` / Scripts

```bash
curl -X POST http://localhost:5011/v1/chat/completions \
  -H "Authorization: Bearer <your-api-key>" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "deepseek-v4-flash",
    "messages": [{"role": "user", "content": "Hello"}],
    "stream": false
  }'
```

### 4.5 Python (OpenAI SDK)

```python
from openai import OpenAI

client = OpenAI(
    api_key="<your-api-key>",
    base_url="http://localhost:5011/v1"
)

response = client.chat.completions.create(
    model="deepseek-v4-flash",
    messages=[{"role": "user", "content": "Hello"}]
)

print(response.choices[0].message.content)
```

### 4.6 TypeScript / Node.js

```typescript
import OpenAI from 'openai';

const client = new OpenAI({
  apiKey: '<your-api-key>',
  baseURL: 'http://localhost:5011/v1',
});

const response = await client.chat.completions.create({
  model: 'deepseek-v4-flash',
  messages: [{ role: 'user', content: 'Hello' }],
});
```

---

## 5. Available Models

Query the models endpoint to see what's available:

```bash
curl -s http://localhost:5011/v1/models \
  -H "Authorization: Bearer <your-api-key>" | jq '.data[] | {id, owned_by}'
```

Current models (configurable via Gateway Admin):

| Model Code | Provider | Notes |
|-----------|----------|-------|
| `deepseek-v4-flash` | OpenCode | Default, fast |
| `gpt-4o` | OpenAI | If configured |
| *(any)* | *(any)* | Add via Gateway Admin UI |

---

## 6. Dashboard & Operations

The Gateway provides a full-featured Blazor dashboard at `http://localhost:5011/`.

| Section | What You Can Do |
|---------|----------------|
| **Dashboard** | View real-time usage stats, request logs, active streams |
| **API Keys** | Create/revoke keys, set model restrictions |
| **Providers** | Enable/disable providers, view model costs |
| **Workflows** | View and trigger n8n workflows (if integrated) |
| **Cost** | Track token usage and costs over time |
| **Logs** | Search full request/response logs |

---

## 7. Architecture

```
┌──────────────┐     ┌─────────────────────────────────────┐
│  AI Agent    │────▶│  ARKANA GATEWAY (:5011)             │
│  (Codex,     │     │                                     │
│  Claude,     │     │  ┌──────────┐  ┌──────────────────┐ │
│  Cline, etc) │     │  │ Dashboard│  │  Auth Middleware │ │
└──────────────┘     │  │ (Blazor) │  │  (API Key Check) │ │
                     │  └──────────┘  └──────────────────┘ │
                     │                                     │
                     │  ┌──────────────────────────────┐   │
                     │  │  Router / Proxy              │   │
                     │  │  ┌──────────┐ ┌──────────┐  │   │
                     │  │  │ OpenCode │ │ OpenAI   │  │   │
                     │  │  │ Backend  │ │ Backend  │  │   │
                     │  │  └──────────┘ └──────────┘  │   │
                     │  └──────────────────────────────┘   │
                     │                                     │
                     │  ┌──────────────────────────────┐   │
                     │  │  n8n Workflow Engine (:5678) │   │
                     │  │  (automation, triggers, cron)│   │
                     │  └──────────────────────────────┘   │
                     └─────────────────────────────────────┘
                                │
                    ┌───────────┴───────────┐
                    ▼                       ▼
            ┌──────────────┐        ┌──────────────┐
            │  PostgreSQL  │        │    Redis     │
            │  (data/logs) │        │  (cache/QS)  │
            └──────────────┘        └──────────────┘
```

---

## 8. Docker Stack

All services run via a single `docker-compose.yml`:

```bash
# Start everything
cd deploy && docker compose up -d

# Services:
# - Gateway:     http://localhost:5011
# - n8n:         http://localhost:5678
# - PostgreSQL:  localhost:5432
# - Redis:       localhost:6379
```

---

## 9. Troubleshooting

### 401 Unauthorized

- Verify the API key is correct and not expired
- Keys in `opencode.json` and `auth.json` must match
- Raw keys are stored as SHA-256 hashes in the database — you can't recover a key, only regenerate

### 429 Rate Limited

- The Gateway's n8n integration authenticates on each request
- Rapid page loads can hit n8n's rate limit (login endpoint)
- Wait 30 seconds and retry

### No Interactivity in Dashboard

- Ensure `blazor.server.js` is served (check browser DevTools → Network tab)
- Hard refresh (Ctrl+Shift+R) to clear cached blazor circuit state
- Container restarts generate new data protection keys — use the `dataprotection` Docker volume to persist them

### Connection Refused

- Ensure Docker containers are running: `docker ps`
- Check the Gateway is accessible: `curl http://localhost:5011/`
- Verify PostgreSQL and Redis are healthy

---

## 10. Quick Start for an Agent

To configure an AI agent to use this Gateway as its provider:

```bash
# 1. Get an API key from the dashboard (http://localhost:5011/api-keys)

# 2. Set environment variables
export OPENAI_API_KEY="<your-api-key>"
export OPENAI_BASE_URL="http://localhost:5011/v1"

# 3. Verify connectivity
curl -s http://localhost:5011/v1/models \
  -H "Authorization: Bearer $OPENAI_API_KEY"

# 4. Send a test chat
curl -X POST http://localhost:5011/v1/chat/completions \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"Hello"}]}'
```

---

> **Repo:** github.com/AI/gateway
> **Dashboard:** http://localhost:5011/
> **Support:** Internal Arkana team
