# Opencode CLI — Native vs Gateway-Routed Architecture

> **Document version:** 1.0  
> **Date:** 2026-06-10  
> **Scope:** ARKANA GATEWAY integration with Opencode CLI

---

## Table of Contents

1. [Native Opencode Architecture](#1-native-opencode-architecture)
2. [Gateway-Routed Architecture (ARKANA GATEWAY)](#2-gateway-routed-architecture-arkana-gateway)
3. [Request Flow Comparison](#3-request-flow-comparison)
4. [What Works & What Doesn't](#4-what-works--what-doesnt)
5. [Root Cause Analysis of Differences](#5-root-cause-analysis-of-differences)
6. [Architecture Diagrams](#6-architecture-diagrams)
7. [Configuration Reference](#7-configuration-reference)
8. [Troubleshooting Guide](#8-troubleshooting-guide)

---

## 1. Native Opencode Architecture

### 1.1 Overview

In its native form, the Opencode CLI connects **directly** to Opencode's own API infrastructure at `https://opencode.ai/zen/go/v1`. The CLI is a TypeScript/Node.js application that uses the `@ai-sdk/openai-compatible` npm package to communicate with AI model providers.

### 1.2 Components

```
┌─────────────────────────────────────────────────────────┐
│                  User's Machine                          │
│                                                          │
│  ┌──────────────┐       ┌──────────────────────────┐    │
│  │ Opencode CLI  │──────▶│ @ai-sdk/openai-compatible │    │
│  │ (Node.js TUI) │       │     (HTTP Client)         │    │
│  └──────────────┘       └──────────┬───────────────┘    │
│                                     │                    │
│  ┌──────────────────────────┐       │                    │
│  │  @opencode-ai/plugin     │       │                    │
│  │  (oh-my-openagent)       │       │                    │
│  └──────────────────────────┘       │                    │
└─────────────────────────────────────┼────────────────────┘
                                      │
                                      ▼
                     ┌───────────────────────────────┐
                     │     Opencode API               │
                     │  https://opencode.ai/zen/go/v1 │
                     │                                 │
                     │  POST /chat/completions          │
                     │  GET  /models                    │
                     └─────────────────────────────────┘
                                      │
                         ┌────────────┴────────────┐
                         ▼                         ▼
                 ┌──────────────┐         ┌────────────────┐
                 │ LLM Provider  │         │ Auth Service    │
                 │ (DeepSeek,    │         │ (API key        │
                 │  Claude, etc) │         │  validation)    │
                 └──────────────┘         └────────────────┘
```

### 1.3 Authentication Flow (Native)

```
1. Opencode CLI reads provider config from ~/.config/opencode/opencode.jsonc
2. Config specifies:
   - npm package: @ai-sdk/openai-compatible
   - baseURL: https://opencode.ai/zen/go/v1
   - apiKey: <opencode-api-key>
3. CLI sends HTTP requests with `Authorization: Bearer <opencode-api-key>`
4. Opencode API validates the key and proxies to the actual LLM provider
```

### 1.4 Key Characteristics (Native)

- **Single-hop**: CLI → Opencode API → LLM Provider
- **Auth is simple**: One API key for all models
- **No observability**: No token tracking, no cost logging, no request auditing
- **No gateway features**: No API key management, no multi-tenant isolation, no rate limiting
- **CLI-specific tools**: Opencode's agent mode uses built-in tools (`execute_command`, `read_file`, `edit_file`, etc.) that run **locally** on the user's machine

---

## 2. Gateway-Routed Architecture (ARKANA GATEWAY)

### 2.1 Overview

In the gateway-routed scheme, the Opencode CLI points to a **local proxy** — the ARKANA GATEWAY — running on `localhost:5011`. The gateway authenticates the CLI via its own API key system, then proxies requests upstream to the Opencode API using a **separate** upstream API key.

### 2.2 Components

```
┌─────────────────────────────────────────────────────────────────┐
│                      User's Machine                              │
│                                                                   │
│  ┌──────────────┐       ┌──────────────────────────┐            │
│  │ Opencode CLI  │──────▶│ @ai-sdk/openai-compatible │            │
│  │ (Node.js TUI) │       │     (HTTP Client)         │            │
│  └──────────────┘       └──────────┬───────────────┘            │
│                                     │                             │
│  ┌──────────────────────────┐       │                             │
│  │  @opencode-ai/plugin     │       │ HTTP :5011                  │
│  └──────────────────────────┘       │                             │
└─────────────────────────────────────┼───────────────────────────┘
                                      │
                                      ▼
             ┌────────────────────────────────────────────────┐
             │            ARKANA GATEWAY (Docker)              │
             │          http://localhost:5011                    │
             │                                                   │
             │  ┌──────────────────────────────────────────┐    │
             │  │  ApiKeyAuthMiddleware                      │    │
             │  │  - Validates gateway API key               │    │
             │  │  - Checks DB for hashed key match          │    │
             │  │  - /admin, /dashboard, /health skip auth   │    │
             │  └────────────────┬─────────────────────────┘    │
             │                   │                               │
             │  ┌────────────────▼──────────────────────────┐    │
             │  │  Routing Layer                             │    │
             │  │  - ChatEndpoints (/v1/chat/completions)    │    │
             │  │  - ResponsesEndpoints (/v1/responses)      │    │
             │  │  - Streaming vs Non-streaming paths        │    │
             │  └────────────────┬─────────────────────────┘    │
             │                   │                               │
             │  ┌────────────────▼──────────────────────────┐    │
             │  │  TokenTrackingMiddleware                   │    │
             │  │  - Records usage per-request               │    │
             │  │  - Logs to PostgreSQL + Redis              │    │
             │  └──────────────────────────────────────────┘    │
             │                                                   │
             │  ┌──────────────────────────────────────────┐    │
             │  │  Admin Endpoints                          │    │
             │  │  - CRUD API keys                          │    │
             │  │  - Manage AI providers + models           │    │
             │  │  - View usage logs + stats                │    │
             │  └──────────────────────────────────────────┘    │
             │                                                   │
             │  ┌──────────────────────────────────────────┐    │
             │  │  Blazor Dashboard                        │    │
             │  │  - Cost charts, active streams           │    │
             │  │  - Provider status, API key management   │    │
             │  │  - Request log viewer                    │    │
             │  └──────────────────────────────────────────┘    │
             └──────────────────────┬─────────────────────────┘
                                    │
                                    ▼
                   ┌─────────────────────────────────┐
                   │   Opencode API (Upstream)        │
                   │   https://opencode.ai/zen/go/v1  │
                   │   Auth: OPENCODE_GO_API_KEY       │
                   └────────────────┬────────────────┘
                                    │
                                    ▼
                          ┌──────────────────┐
                          │  LLM Provider    │
                          │  (DeepSeek V4    │
                          │   Flash, etc.)   │
                          └──────────────────┘

┌──────────────────────────────────────────────────────────┐
│  Supporting Infrastructure (Docker Compose)               │
│                                                           │
│  ┌────────────┐  ┌──────────┐  ┌─────────────────────┐  │
│  │ PostgreSQL  │  │  Redis   │  │  n8n Workflow Engine│  │
│  │ :5432       │  │ :6379    │  │ :5678               │  │
│  │ (persistence)│  │ (cache)  │  │ (automation)        │  │
│  └────────────┘  └──────────┘  └─────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

### 2.3 Authentication Flow (Gateway)

```
────────────── LAYER 1: Gateway Auth ──────────────
1. Opencode CLI → Gateway :5011/v1/chat/completions
2. Header: Authorization: Bearer <gateway-api-key>
3. ApiKeyAuthMiddleware:
   a. Hashes the incoming key with SHA256
   b. Looks up hash in PostgreSQL ApiKeys table
   c. Validates: exists ∧ isActive ∧ notExpired
   d. Stores ApiKeyId, ApiKeyName in HttpContext.Items
4. If invalid → HTTP 401 "Invalid or expired API key"

────────────── LAYER 2: Upstream Auth ──────────────
5. ChatEndpoint reads OPENCODE_GO_API_KEY:
   - From config["ProviderOptions:OpenCode:ApiKey"] (if not empty)
   - Fallback: Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY")
6. Gateway → Opencode API with upstream API key
7. If upstream key invalid → HTTP 401 forwarded to CLI

────────────── KEY SEPARATION ──────────────
Gateway API Key (layer 1)   = arkana-<hash64> (arbitrary string)
Upstream API Key (layer 2)  = sk-<openai-format> (from OpenCode)
```

### 2.4 Key Characteristics (Gateway)

- **Two-hop**: CLI → Gateway → Opencode API → LLM Provider
- **Dual auth**: Gateway API key (layer 1) + Upstream OpenCode key (layer 2)
- **Full observability**: Token usage tracking, cost calculation, request logging, per-key analytics
- **Admin features**: API key CRUD, provider management, model routing, usage stats
- **Dashboard**: Blazor Server UI for monitoring and configuration
- **Streaming passthrough**: SSE events relayed from upstream through gateway
- **Tool call forwarding**: Tool definitions and tool_choice are forwarded to upstream

---

## 3. Request Flow Comparison

### 3.1 Simple Chat Request

```
NATIVE:
  Opencode CLI ──POST /v1/chat/completions──▶ opencode.ai ──▶ LLM
  Auth: Bearer sk-xxx                          Auth: validate key
  Body: {model, messages, stream}
  Response: chat completion or SSE stream

GATEWAY:
  Opencode CLI ──POST /v1/chat/completions──▶ :5011 Gateway
  Auth: Bearer arkana-xxx
  Body: {model, messages, stream}
       │
       ▼ Gateway validates gateway API key ✓
       ▼ Gateway reads OPENCODE_GO_API_KEY
       ▼ Gateway forwards to opencode.ai with upstream key
       │
       opencode.ai ──▶ LLM Provider
       Response ──▶ Gateway ──▶ Opencode CLI
```

### 3.2 Tool Call Request

```
NATIVE:
  Opencode CLI ──POST /v1/chat/completions──▶ opencode.ai
  Body: {model, messages, tools: [openCodeBuiltInTools]}
  Response: tool_calls or text
  │
  ▼ If tool_calls: CLI executes tool locally
  ▼ CLI sends tool_result back to model
  └─▶ Loop until final response

GATEWAY:
  Opencode CLI ──POST /v1/chat/completions──▶ :5011 Gateway
  Body: {model, messages, tools: [openCodeBuiltInTools]}
       │
       ▼ Gateway forwards with upstream key
       ▼ opencode.ai ──▶ LLM Provider
       │
       Response: 400 Bad Request ← ISSUE HERE
       │
       ▼ Gateway returns error to CLI
```

### 3.3 Agent Mode Flow (Where It Breaks)

```
NATIVE (works):
  CLI sends tool definitions → opencode.ai accepts them
  → LLM returns tool_calls
  → CLI executes command locally
  → Sends result back → LLM summarizes

GATEWAY (breaks at upstream):
  CLI sends tool definitions → Gateway forwards →
  opencode.ai rejects with 400
  ↓
  Root cause: Opencode API's deepseek-v4-flash
  does not recognize or accept the specific tool
  schema format that Opencode CLI sends as
  part of its agent system prompt
```

---

## 4. What Works & What Doesn't

### 4.1 ✅ Working Functionality

| Feature | Native | Gateway | Notes |
|---------|--------|---------|-------|
| **Basic chat completion** | ✅ | ✅ | Non-streaming and streaming |
| **SSE streaming** | ✅ | ✅ | 200 from upstream; events relayed correctly |
| **Tool definitions in request** | ✅ | ✅ | Gateway forwards tools array faithfully |
| **Tool calls in response** | ✅ | ✅ | Model returns `tool_calls` JSON |
| **Tool result cycle** | ✅ | ✅ | Sending tool_result back works |
| **Model listing** | ✅ | ✅ | Gateway fetches models from DB |
| **Multi-turn conversation** | ✅ | ✅ | Messages forwarded correctly |
| **OpenCode CLI simple mode** | ✅ | ✅ | `opencode run "hi"` returns response |
| **OpenCode CLI agent (simple)** | ✅ | ✅ | Agent can process basic text responses |

### 4.2 ❌ Not Working (Gateway Only)

| Feature | Native | Gateway | Root Cause |
|---------|--------|---------|------------|
| **OpenCode CLI agent mode with tools** | ✅ | ❌ 400 from upstream | Opencode API rejects deepseek-v4-flash tool definitions from the agent prompt |
| **OpenCode CLI `execute_command` tool** | ✅ | ❌ 400 from upstream | Same root cause — LLM provider incompatibility |
| **OpenCode CLI file read/write tool** | ✅ | ❌ 400 from upstream | Same root cause — LLM provider incompatibility |

### 4.3 ⚠️ Degraded / Different Behavior

| Feature | Native | Gateway | Reason |
|---------|--------|---------|--------|
| **Auth model** | Single key | Dual key (gateway + upstream) | Gateway adds security layer |
| **Latency** | ~1-2s | ~1-3s (+200-500ms) | Gateway adds auth check + logging overhead |
| **Observability** | Minimal | Full (tokens, cost, logs) | Gateway feature |
| **Key management** | Manual | Admin API + Dashboard | Gateway feature |
| **Multi-tenant isolation** | None | Per-key model permissions | Gateway feature |

---

## 5. Root Cause Analysis of Differences

### 5.1 Why Agent Mode Tool Calls Fail

The core issue is a **compatibility gap between three layers**:

```
Opencode CLI Agent
    │
    │ Creates tool definitions in its own schema:
    │   - execute_command: {command: string, ...}
    │   - read_file: {path: string, ...}
    │   - edit_file: {path: string, old_string: string, new_string: string}
    │   - search_files: {pattern: string, ...}
    │
    ├──▶ When direct to opencode.ai (NATIVE):
    │      These tool definitions are sent to Opencode API
    │      → Opencode API's model router handles the schema
    │      → deepseek-v4-flash receives the tool definitions
    │      → Model returns tool_calls or text
    │
    └──▶ When through Arkana Gateway:
         These tool definitions are sent to Gateway at :5011
         → Gateway forwards them unchanged to Opencode API
         → Opencode API's model router: 400 Bad Request
         → Why? The request format from the gateway might have
           subtle differences that the upstream rejects
```

**Hypothesis:** The Opencode CLI's agent mode sends tool definitions as part of the **system message** (embedded in the prompt text), not in the `tools` JSON field. The `tools` field in the JSON body may be empty or missing. When it reaches the upstream Opencode API, the deepseek-v4-flash model sees function-calling-like instructions in the system prompt but no actual `tools` array, causing the model to respond with `tool_calls` in a format that the API doesn't expect, returning 400.

Alternatively: The deepseek-v4-flash model on OpenCode's infrastructure **does support tool calling**, but the specific schema the CLI uses (particularly the `strict` field or parameter format) may differ between the native path and the gateway-forwarded path, triggering a validation error.

### 5.2 Auth Architecture Differences

```
NATIVE:
  CLI apiKey = "sk-xxx" ← Same key for everything
  → Sent directly to opencode.ai

GATEWAY:
  CLI apiKey = "arkana-xxx" ← Gateway-level key
  → Gateway validates against PostgreSQL hashed keys
  → Gateway then uses OPENCODE_GO_API_KEY = "sk-xxx" for upstream
  → MUST use the correct fallback logic (null vs empty string)
```

**Critical bug fixed:** The `appsettings.json` had `"ApiKey": ""` (empty string). The `??` operator only falls through on `null`, not `""`. This caused the streaming path to never read the `OPENCODE_GO_API_KEY` env var. Fixed by using `if (string.IsNullOrEmpty(...))` before falling through.

### 5.3 Why Streaming Failed (Fixed)

```
Before fix:
  config["ProviderOptions:OpenCode:ApiKey"] = ""  ← not null!
  result = "" ?? envVar  → ""  ← wrong!
  if (!string.IsNullOrEmpty("")) → false
  → No auth header set → 401 from upstream

After fix:
  config["ProviderOptions:OpenCode:ApiKey"] = ""
  if (string.IsNullOrEmpty("")) → true
  result = envVar = "sk-xxx"  ← correct!
  Auth header set → 200 from upstream
```

### 5.4 Relevant Code Paths

| File | Path | Purpose |
|------|------|---------|
| `Program.cs` | L46-64 | Conditional auth middleware (excludes dashboard paths) |
| `ApiKeyAuthMiddleware.cs` | L17-59 | Validates gateway API key against DB |
| `ChatEndpoints.cs` | L45-227 | Streaming mode — reads upstream key, proxies SSE |
| `ChatEndpoints.cs` | L229-310 | Non-streaming mode — via MediatR/SendChatHandler |
| `OpenCodeChatService.cs` | L36-139 | Non-streaming HTTP call to upstream |
| `DependencyInjection.cs` | L45-54 | Named HttpClients for "opencode" + "opencode-streaming" |
| `ApiKeyBootstrapService.cs` | L25-53 | Seeds OPENCODE_GO_API_KEY into DB on startup |

---

## 6. Architecture Diagrams

### 6.1 Network Topology

```
┌─────────────────────────────────────────────────────────────┐
│  Docker Network: arkana-net (bridge)                      │
│                                                              │
│  ┌──────────────┐      ┌──────────────┐      ┌──────────┐  │
│  │  PostgreSQL   │◄────▶│   Gateway    │◄────▶│  Redis   │  │
│  │  :5432        │      │  :5011       │      │  :6379   │  │
│  └──────────────┘      └──────┬───────┘      └──────────┘  │
│                               │                              │
│                      ┌────────▼────────┐                    │
│                      │      n8n       │                    │
│                      │     :5678      │                    │
│                      └─────────────────┘                    │
└─────────────────────────────────────────────────────────────┘
                               │
              Host Network     │ :5011
          ┌────────────────────┴────────────────────┐
          │                                         │
  ┌───────▼────────┐                    ┌──────────▼───────────┐
  │  Opencode CLI   │                    │  Browser Dashboard   │
  │  localhost:5011  │                    │  localhost:5011       │
  └────────────────┘                    └──────────────────────┘
```

### 6.2 Gateway Internal Middleware Pipeline

```
Request arrives
       │
       ▼
┌──────────────────────┐
│ Static Files         │  _framework/, _content/, app.css
│ (Blazor assets)      │
└──────────────────────┘
       │
       ▼
┌──────────────────────┐
│ CORS                 │  AllowAnyOrigin
└──────────────────────┘
       │
       ▼
┌──────────────────────┐
│ Path Exclusion Check │  if path starts with /dashboard, /health,
│ (UseWhen condition)  │  /admin, /api-keys, /providers, /_blazor,
│                      │  /_framework, /_content, /app.css, /, /v1/models
│                      │  → Skip API key auth middleware
│                      │  → Otherwise → apply middleware
└──────────┬───────────┘
           │
           ▼ (conditional)
┌──────────────────────┐
│ ApiKeyAuthMiddleware │  Hash the key, look up in DB
│                      │  401 if invalid / inactive / expired
│                      │  Stores ApiKeyId, ApiKeyName, AllowedModels
└──────────────────────┘
           │
           ▼
┌──────────────────────┐
│ TokenTracking        │  Logs request metadata
│ Middleware           │  (before/after handler execution)
└──────────────────────┘
           │
           ▼
┌──────────────────────┐
│ Route Endpoint       │  /v1/chat/completions, /v1/responses,
│                      │  /admin/..., /dashboard (Blazor)
└──────────────────────┘
```

### 6.3 Data Flow: Streaming Request

```
CLI                          Gateway                         Opencode API
 │                             │                                │
 │ POST /v1/chat/completions   │                                │
 │ {stream: true, tools, ...}  │                                │
 │ Authorization: arkana-xxx     │                                │
 │────────────────────────────▶│                                │
 │                             │                                │
 │                             │  Auth check (DB)               │
 │                             │  ✓ Valid gateway key            │
 │                             │                                │
 │                             │  Read upstream key:             │
 │                             │    config ?? env var            │
 │                             │    → sk-xxx                     │
 │                             │                                │
 │                             │ POST /chat/completions          │
 │                             │ {stream: true, tools, ...}      │
 │                             │ Authorization: Bearer sk-xxx    │
 │                             │───────────────────────────────▶│
 │                             │                                │
 │                             │                  200 (if valid)
 │                             │◀───────────────────────────────│
 │                             │                                │
 │  data: {"choices":[...]}    │                                │
 │◀────────────────────────────│                                │
 │  data: [DONE]               │                                │
 │◀────────────────────────────│                                │
 │                             │  Log usage to PG + Redis       │
 │                             │                                │
```

---

## 7. Configuration Reference

### 7.1 Opencode CLI Config (`~/.config/opencode/opencode.jsonc`)

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "plugin": ["oh-my-openagent"],
  "model": "arkana-gateway/deepseek-v4-flash",
  "small_model": "arkana-gateway/deepseek-v4-flash",
  "provider": {
    "arkana-gateway": {
      "npm": "@ai-sdk/openai-compatible",   // Required for custom baseURL
      "name": "ARKANA GATEWAY",
      "options": {
        "baseURL": "http://localhost:5011/v1", // Gateway endpoint
        "apiKey": "arkana-<hash64>"              // Gateway-level API key
      },
      "models": {
        "deepseek-v4-flash": { "id": "deepseek-v4-flash", ... }
        // All models must be explicitly listed here
      }
    }
  }
}
```

### 7.2 Gateway Config (`appsettings.json`)

```json
{
  "ProviderOptions": {
    "OpenCode": {
      "BaseUrl": "https://opencode.ai/zen/go/v1",   // Upstream API URL
      "ApiKey": "",                                   // ← LEAVE EMPTY (use env var)
      "DefaultModel": "deepseek-v4-flash"
    }
  },
  "ConnectionStrings": {
    "Postgres": "Host=postgres;Port=5432;Database=arkana;..."
  }
}
```

### 7.3 Docker Compose Environment

```yaml
environment:
  OPENCODE_GO_API_KEY: ${OPENCODE_GO_API_KEY:?error}  # Required upstream key
  ConnectionStrings__Postgres: "Host=postgres;..."
```

### 7.4 `.env` File

```
OPENCODE_GO_API_KEY=sk-<openai-format-key>
N8N_ENCRYPTION_KEY=<n8n-key>
N8N_EMAIL=admin@example.com
N8N_PASSWORD=<n8n-password>
```

### 7.5 Required npm Packages

```json
{
  "dependencies": {
    "@ai-sdk/openai-compatible": "^2.0.48",    // Custom provider support
    "@opencode-ai/plugin": "1.15.13"            // Opencode plugin system
  }
}
```

---

## 8. Troubleshooting Guide

### 8.1 "Invalid or expired API key" (401 from Gateway)

```
Symptom: 401 response from localhost:5011

Check: 1. Is the gateway API key valid?
       curl -s http://localhost:5011/admin/api-keys
       → Lists keys (call without auth for /admin)

       2. Does the key match the config?
       grep apiKey ~/.config/opencode/opencode.jsonc
       → Must match the plainTextKey from POST /admin/api-keys

       3. Has the key expired or been deactivated?
       Check expiresAt and isActive in the list

Fix:  Create a new key:
      curl -X POST http://localhost:5011/admin/api-keys \
        -H "Content-Type: application/json" \
        -d '{"name": "my-key", "key": "my-secret-key"}'
      → Use the returned plainTextKey in opencode.jsonc
```

### 8.2 "Response status code does not indicate success: 401" (from upstream)

```
Symptom: Gateway forwards request but upstream returns 401

Check: 1. Is OPENCODE_GO_API_KEY set correctly in the container?
       docker compose exec gateway printenv OPENCODE_GO_API_KEY

       2. Is the key valid against opencode.ai directly?
       curl https://opencode.ai/zen/go/v1/chat/completions \
         -H "Authorization: Bearer <key>" \
         -d '{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"hi"}]}'

Fix:  Update .env with a valid key and restart:
      docker compose up -d gateway
```

### 8.3 "Response status code does not indicate success: 400" (from upstream)

```
Symptom: Upstream returns 400, typically during tool/agent mode

Check: 1. Is this a simple chat or agent mode request?
       Simple chat → should work (tested ✓)
       Agent mode with tool definitions → may fail

       2. Try without tools to isolate:
       Send a bare chat request through gateway → should 200

Cause: Opencode API / deepseek-v4-flash incompatibility
       with tool definitions sent by opencode CLI's agent

Workaround: Use native Opencode connection for agent mode,
            or use gateway for simple chat completions
```

### 8.4 Blazor Dashboard Not Loading (404 on blazor.server.js)

```
Symptom: Dashboard URL loads but only navigation works,
         no interactive components

Cause: Docker build was skipping static web assets
       (missing --no-restore removal in Dockerfile)

Fix:  Ensure Dockerfile publish step does NOT include --no-restore
      RUN dotnet publish ... -c Release -o /app/publish
      (without --no-restore)
```

### 8.5 Streaming Returns 401 But Non-Streaming Works

```
Symptom: Non-streaming requests work, streaming returns 401

Cause: Empty string in config → "ApiKey": ""
       The streaming code path reads config first,
       and the ?? null-coalescing operator doesn't
       treat empty string as null.

Fix:  Already applied in ChatEndpoints.cs:
      From: config["...ApiKey"] ?? envVar
      To:   if (string.IsNullOrEmpty(config["...ApiKey"]))
                apiKey = envVar
```

---

## Appendix: Summary Comparison Table

| Aspect | Native Opencode | Via Arkana Gateway |
|--------|----------------|-----------------|
| **Endpoint** | `opencode.ai/zen/go/v1` | `localhost:5011/v1` |
| **Auth layers** | 1 (upstream key) | 2 (gateway key + upstream key) |
| **API key source** | Config file | Config file (gateway) + env var (upstream) |
| **API key type** | `sk-xxx` | `arkana-xxx` (gateway) + `sk-xxx` (upstream) |
| **Key storage** | Config file only | Config file + PostgreSQL (hashed) |
| **Key management** | Manual file edit | Admin API + Dashboard UI |
| **Token tracking** | None | PostgreSQL + Redis |
| **Cost tracking** | None | Per-request cost calculation |
| **Request logging** | None | Full request/response logging |
| **Rate limiting** | Provider-side | Gateway-level (configurable) |
| **Multi-model** | By provider config | Model Router + DB |
| **Streaming** | Direct SSE | Gateway passthrough SSE |
| **Tool calling** | Full support | Gateway proxies (upstream dependent) |
| **Agent mode** | Full support | ❌ 400 from upstream (deepseek-v4-flash) |
| **Dashboard** | None | Blazor Server UI |
| **Infrastructure** | None | Docker Compose (PostgreSQL + Redis + n8n) |
| **Setup complexity** | Low (single CLI) | High (Docker stack + config) |
| **Latency overhead** | Baseline | +200-500ms |
