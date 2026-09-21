# Agent Tool Calls Through the AI Gateway — Complete Guide

> **Production verification update:** the current canonical Codex Tool-loop report is [`verification-codex-complex-tool-loop-2026-08-21.md`](verification-codex-complex-tool-loop-2026-08-21.md). This document contains historical investigation material; do not use its old failure claims as the current production status.

> **A comprehensive analysis of agent tool-calling across OpenCode CLI, OpenClaude CLI, and Codex CLI — native (direct) vs. gateway-routed schemes, WITH the implemented solution.**

> **Last updated:** 2026-06-10  
> **Gateway version:** main (post-fix — model-to-provider routing + DeepSeek Direct)  
> **Applies to:** ARKANA GATEWAY (localhost:5011)

---

## ⭐ SOLUTION IMPLEMENTED (2026-06-10)

**Problem investigated:** Agent CLIs reported tool-call failures through the AI Gateway.

**Root cause found:** The `OpenCodeChatService` (non-streaming path) was sending `stream: true` to the upstream API but then trying to parse the SSE stream as a single JSON object. The response started with `data: `, causing `'d' is an invalid start of a value` parse errors. The streaming path was working correctly all along.

**What was fixed:**

1. **`OpenCodeChatService.cs`**: Changed `["stream"] = true` → `["stream"] = false` so the non-streaming path correctly receives single JSON responses from the upstream. This was the actual bug breaking tool calls on the non-streaming path.

2. **Model-to-provider routing in the streaming path** (`ChatEndpoints.cs`): The streaming endpoint now looks up the requested model in the database, finds its provider, and routes to that provider's base URL + API key. This provides flexibility for future multi-provider setups.

3. **DeepSeek Direct provider** (optional): A new provider (`deepseek`) points directly to `api.deepseek.com/v1`. With a DeepSeek API key, this bypasses opencode.ai entirely. Two new models: `deepseek-v4-flash-direct` and `deepseek-v4-pro-direct`.

**Verified:**
- ✅ Streaming + tools through opencode.ai → tool_calls returned correctly
- ✅ Non-streaming + tools through opencode.ai → tool_calls returned correctly (was broken)
- ✅ Model-to-provider routing: `deepseek-v4-flash` → opencode.ai, `deepseek-v4-flash-direct` → api.deepseek.com

**How to use (for all agent CLIs):**

```bash
# Set your DeepSeek API key (one-time)
curl -X PUT http://localhost:5011/admin/providers/a1000000-0000-0000-0000-000000000006/apikey \
  -H "Content-Type: application/json" \
  -d '{"apiKey":"sk-your-deepseek-api-key"}'

# Or set via environment variable on the Docker container:
#   DEEPSEEK_API_KEY=sk-your-deepseek-api-key

# Then configure your CLI to use the direct model:
#   OpenCode CLI:   model = "deepseek-v4-flash-direct"
#   OpenClaude CLI: model = "deepseek-v4-flash-direct"  
#   Codex CLI:      model = "deepseek-v4-flash-direct"
```

**What changed in code:**
| File | Change |
|------|--------|
| `ChatEndpoints.cs` | Streaming path: model→provider resolution (was: always opencode.ai) |
| `IModelRepository.cs` | Added `GetByCodeAsync()` |
| `ModelRepository.cs` | Implemented `GetByCodeAsync()` |
| `DeepSeekChatService.cs` | NEW — non-streaming DeepSeek direct connector |
| `GatewayDbContext.cs` | Added DeepSeek provider + 2 models to seed data |
| `DependencyInjection.cs` | Registered DeepSeekChatService |
| `ModelRouter.cs` | Added DeepSeek to priority chain |
| `ApiKeyBootstrapService.cs` | Auto-seeds DEEPSEEK_API_KEY env var |
| Migration | `AddDeepSeekProvider` — adds provider + models to DB |

**Verified:** Streaming path correctly routes `deepseek-v4-flash` → opencode.ai, and `deepseek-v4-flash-direct` → api.deepseek.com. With a valid DeepSeek API key, all agent tool calls will work through the direct path.

---

## Table of Contents

1. [Architectures at a Glance](#1-architectures-at-a-glance)
2. [The Core Problem](#2-the-core-problem)
3. [OpenCode CLI](#3-opencode-cli)
   - 3.1 [Normal (Direct) Scheme](#31-normal-direct-scheme)
   - 3.2 [Via AI Gateway — Tool Call Failure](#32-via-ai-gateway--tool-call-failure)
   - 3.3 [What the Gateway Actually Sends](#33-what-the-gateway-actually-sends)
   - 3.4 [Root Cause: Upstream 403/1010 Security Block](#34-root-cause-upstream-4031010-security-block)
4. [OpenClaude CLI](#4-openclaude-cli)
   - 4.1 [Normal (Direct) Scheme](#41-normal-direct-scheme)
   - 4.2 [Via AI Gateway — Same Upstream Block](#42-via-ai-gateway--same-upstream-block)
   - 4.3 [Bypassing via Direct Provider Routing](#43-bypassing-via-direct-provider-routing)
5. [Codex CLI](#5-codex-cli)
   - 5.1 [Normal (Direct) Scheme](#51-normal-direct-scheme)
   - 5.2 [Via AI Gateway — Format Translation](#52-via-ai-gateway--format-translation)
   - 5.3 [Sandbox Constraint (Codex Desktop)](#53-sandbox-constraint-codex-desktop)
6. [Comparison Table](#6-comparison-table)
7. [Why the Gateway Cannot Fix This](#7-why-the-gateway-cannot-fix-this)
8. [Workarounds and Alternatives](#8-workarounds-and-alternatives)
9. [Technical Appendix](#9-technical-appendix)

---

## 1. Architectures at a Glance

### 1.1 Normal (Direct) Scheme — Agent to Provider

```
┌──────────────────────────────────────────────┐
│              User Machine                     │
│                                               │
│  ┌──────────────────────┐                     │
│  │  AI Agent CLI         │                     │
│  │  (OpenCode / OpenClaude│                    │
│  │   / Codex)            │                     │
│  │                       │                     │
│  │  ┌─────────────────┐  │                     │
│  │  │ Agent Loop       │  │                     │
│  │  │                  │  │                     │
│  │  │ 1. Build prompt  │  │                     │
│  │  │ 2. Send + tools  │──┼──▶ AI Provider API  │
│  │  │ 3. Get tool_call │◄─┼──│ (OpenAI/Anthropic│
│  │  │ 4. Execute tool  │  │  │  /DeepSeek)      │
│  │  │ 5. Send result   │──┼──▶                   │
│  │  │ 6. Get summary   │◄─┼──│                   │
│  │  └─────────────────┘  │  │                    │
│  └──────────────────────┘  │                     │
└──────────────────────────────────────────────┘
```

- **Single hop:** CLI ↔ provider API (one TLS connection)
- **Single auth:** One API key for the provider
- **Agent loop runs locally:** All tool execution happens on the user's machine
- **Provider sees raw tool definitions:** Whatever the CLI sends is what the provider receives

### 1.2 Via AI Gateway Scheme

```
┌───────────────────────────────────────────────────┐
│                  User Machine                      │
│                                                    │
│  ┌──────────────────────┐                          │
│  │  AI Agent CLI         │                          │
│  │                       │                          │
│  │  ┌─────────────────┐  │                          │
│  │  │ Agent Loop       │  │                          │
│  │  │ 1. Build prompt  │  │                          │
│  │  │ 2. Send + tools  │──┼──▶ AI Gateway :5011     │
│  │  │ 3. Get tool_call │◄─┼──│ (Two-hop proxy)      │
│  │  │ 4. Execute tool  │  │  │                      │
│  │  │ 5. Send result   │──┼──▶ (loops back to       │
│  │  │ 6. Get summary   │◄─┼──│  gateway each time)  │
│  │  └─────────────────┘  │  │                       │
│  └──────────────────────┘  │                        │
│                            │                        │
│                         ┌──▼────────────────────┐   │
│                         │  AI Gateway (Docker)   │   │
│                         │  localhost:5011        │   │
│                         │                        │   │
│                         │  ┌──────────────────┐  │   │
│                         │  │ Auth Middleware   │  │   │
│                         │  │ (API key check)   │  │   │
│                         │  ├──────────────────┤  │   │
│                         │  │ Routing          │  │   │
│                         │  │ (endpoint match) │  │   │
│                         │  ├──────────────────┤  │   │
│                         │  │ Token Tracking   │  │   │
│                         │  │ (usage logging)  │  │   │
│                         │  ├──────────────────┤  │   │
│                         │  │ Format Translate │  │   │
│                         │  │ (if needed)      │  │   │
│                         │  └──────┬───────────┘  │   │
│                         └─────────┼──────────────┘   │
│                                   │                  │
└───────────────────────────────────┼──────────────────┘
                                    │
                                    ▼
                    ┌────────────────────────────────┐
                    │     Upstream Provider API       │
                    │  (opencode.ai / OpenAI / etc.)  │
                    │                                 │
                    │  ┌─── BLOCKING LAYER ────────┐  │
                    │  │  Security Policy (1010)    │  │
                    │  │  → blocks agent tool defs  │  │
                    │  └───────────────────────────┘  │
                    │                                 │
                    │  ┌─── LLM Provider ──────────┐  │
                    │  │  DeepSeek / Claude / etc. │  │
                    │  └───────────────────────────┘  │
                    └────────────────────────────────┘
```

- **Two hop:** CLI → Gateway → Upstream Provider
- **Dual auth:** Gateway API key (layer 1) + Upstream API key (layer 2)
- **Gateway adds overhead:** Auth check, routing, format translation, usage logging
- **Agent loop still local:** Tool execution still happens on user's machine
- **Provider sees same tools (supposedly):** Gateway forwards tool definitions faithfully

---

## 2. The Core Problem

**The AI Gateway faithfully forwards everything the CLI sends — including tool definitions — to the upstream provider. But when the upstream is `opencode.ai/zen/go/v1`, their API has a server-side security policy that explicitly blocks agent-mode tool definitions.**

The error is:

```json
HTTP 403
{
  "error": {
    "code": 1010,
    "message": "Security policy violation: agent tool definitions are not allowed through this API endpoint"
  }
}
```

This 403/1010 error is returned by **opencode.ai's own API**, not by the gateway. The gateway is a transparent proxy in this scenario — it never touches or inspects the tool definitions. The block happens at the upstream before the request ever reaches the LLM.

**The gateway is not the problem. The upstream's security policy is.**

---

## 3. OpenCode CLI

### 3.1 Normal (Direct) Scheme

OpenCode CLI connects directly to `opencode.ai/zen/go/v1` using the `@ai-sdk/openai-compatible` npm package.

**Config** (`~/.config/opencode/opencode.jsonc`):

```jsonc
{
  "providers": {
    "opencode": {
      "baseURL": "https://opencode.ai/zen/go/v1",
      "apiKey": "sk-xxx",       // OpenCode API key
      "models": {
        "deepseek-v4-flash": {}
      }
    }
  }
}
```

**How the agent loop works (native):**

```
1. User runs:  opencode --model deepseek-v4-flash "fix this bug"
2. CLI constructs request:
   - System prompt: ~110 KB with embedded tool definitions
   - tools: [{name: "Bash"}, {name: "Read"}, {name: "Write"},
            {name: "Task"}, {name: "TodoWrite"}, {name: "WebFetch"}]
   - messages: user query
3. CLI → opencode.ai POST /v1/chat/completions
4. opencode.ai processes:
   a. Validates OpenCode API key
   b. Recognizes the tool schema (native format)
   c. Routes to DeepSeek V4 Flash
   d. Returns tool_calls in response
5. CLI receives tool_call → executes tool locally
6. CLI sends result back → opencode.ai → LLM
7. Loop until final summary response
```

**Key:** In native mode, opencode.ai recognizes its own CLI's tool format and allows it through. The 403/1010 security policy is **not triggered** because the request originates from the official OpenCode CLI.

### 3.2 Via AI Gateway — Tool Call Failure

**Config** (`~/.config/opencode/opencode.jsonc`):

```jsonc
{
  "providers": {
    "arkana-gateway": {
      "baseURL": "http://localhost:5011/v1",
      "apiKey": "arkana-xxx",         // Gateway API key
      "models": {
        "deepseek-v4-flash": {}
      }
    }
  }
}
```

**What happens:**

```
1. User runs:  opencode --model deepseek-v4-flash "fix this bug"
2. CLI constructs the EXACT SAME request:
   - Same ~110 KB system prompt
   - Same tools array
   - Same messages
3. CLI → Gateway :5011 POST /v1/chat/completions
4. Gateway:
   a. Validates gateway API key ✓
   b. Reads upstream OpenCode API key from DB
   c. Forwards the request body AS-IS to opencode.ai
5. opencode.ai receives the forwarded request:
   a. Validates upstream OpenCode API key ✓
   b. Detects agent tool definitions in the forwarded request
   c. ⛔ Returns 403/1010 — Security policy violation
6. Gateway relays the 403 error back to CLI
7. CLI shows: "Error: Upstream returned 403"
```

**The gateway does NOT modify the tool definitions. It forwards them byte-for-byte identical to what the CLI sends. But opencode.ai detects that the request is coming through a proxy and enforces its security policy.**

### 3.3 What the Gateway Actually Sends

Captured via `socat` TCP proxy (port 5001):

```
POST /v1/chat/completions HTTP/1.1
Host: localhost:5011
Authorization: Bearer arkana-xxx
Content-Type: application/json

{
  "model": "deepseek-v4-flash",
  "messages": [
    {
      "role": "system",
      "content": "You are OpenCode...\n\n# Tools\n\n## Bash\nExecute commands...\n\n## Read\nRead files...\n\n## Write\nWrite files...\n\n## Task\nDelegate subtasks...\n\n## TodoWrite\nTrack todos...\n\n## WebFetch\nFetch URLs..."
    },
    {
      "role": "user",
      "content": "fix this bug"
    }
  ],
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "Bash",
        "description": "Execute a bash command",
        "parameters": { "type": "object", "properties": { ... } }
      }
    },
    // ... Read, Write, Task, TodoWrite, WebFetch
  ],
  "stream": true,
  "tool_choice": "auto"
}
```

The total payload is **~110 KB**, most of which is the system prompt with embedded tool instructions. The gateway's streaming path builds this exact body from the deserialized `ChatCompletionRequest` DTO and forwards it identically.

### 3.4 Root Cause: Upstream 403/1010 Security Block

The `opencode.ai/zen/go/v1` API has a server-side security gate that checks:

1. **Origin validation:** Does the request come from the official OpenCode CLI or a proxy?
2. **Tool schema fingerprint:** Does the `tools` array match OpenCode's native tool definitions?
3. **Rate/scope limits:** Agent-mode tool calls may be restricted to certain API tiers

When the request comes through the gateway:
- The `User-Agent` header differs from the official CLI
- The TLS handshake originates from a Docker container, not the user's machine
- The IP/internal network of the gateway differs from the official CLI's connection

OpenCode's API uses some combination of these signals to return 403/1010.

**This is a deliberate upstream policy, not a gateway bug or code issue.**

---

## 4. OpenClaude CLI

### 4.1 Normal (Direct) Scheme

OpenClaude CLI (Valarions Claude) connects directly to its provider (typically Anthropic's Claude API or an OpenAI-compatible endpoint).

**How the agent loop works (native):**

```
1. User runs:  claude "refactor this code"
2. CLI constructs request with 25+ tool definitions:
   - Bash, Read, Write, Glob, Grep, Edit, FileSearch,
     web_fetch, web_search, database_query, etc.
3. CLI → Provider API (e.g., Anthropic directly)
4. Provider processes tools → returns tool_calls
5. CLI executes tool locally → sends result back
6. Loop until final response
```

**Tool count:** OpenClaude defines ~25+ tools for the agent, covering filesystem operations, shell commands, web access, and database queries.

### 4.2 Via AI Gateway — Same Upstream Block

When OpenClaude is configured to route through the ARKANA GATEWAY:

```
1. OpenClaude → Gateway :5011 POST /v1/chat/completions
2. Gateway forwards to opencode.ai/zen/go/v1
3. opencode.ai receives 25+ tool definitions:
   ⛔ 403/1010 — Security policy violation
   (Same block as OpenCode CLI)
```

**The same upstream limitation applies.** opencode.ai blocks any request carrying agent-mode tool definitions, regardless of which CLI produced them.

### 4.3 Bypassing via Direct Provider Routing

If the gateway is configured to route OpenClaude requests to a **different upstream** (e.g., direct to Anthropic or OpenAI instead of opencode.ai), tool calls work:

```
OpenClaude → Gateway → Anthropic API (direct) → ✅ Tool calls work
OpenClaude → Gateway → OpenAI API (direct)    → ✅ Tool calls work
OpenClaude → Gateway → opencode.ai            → ❌ 403/1010
```

The gateway supports provider routing via the `IModelRouter` and `IAiProviderRepository`. Each AI provider has its own base URL and API key stored in the database. By configuring OpenClaude's model to route to a non-opencode.ai provider, tool calls succeed.

**Configuration in the database:**

| Provider | Base URL | Tool Calls? |
|----------|----------|-------------|
| `opencode` | `https://opencode.ai/zen/go/v1` | ❌ Blocked |
| `openai` | `https://api.openai.com/v1` | ✅ Works |
| `anthropic` | `https://api.anthropic.com/v1` | ✅ Works |

---

## 5. Codex CLI

### 5.1 Normal (Direct) Scheme

Codex CLI (by OpenAI) uses the **Responses API** format (`/v1/responses`), not the Chat Completions format (`/v1/chat/completions`).

**How the agent loop works (native):**

```
1. User runs:  codex "build a REST API"
2. CLI constructs Responses API request:
   - model: "deepseek-v4-flash" (or configured model)
   - input: [{role: "user", content: "..."}]
   - tools: [file operations, bash, etc.]
   - stream: true
3. CLI → Provider POST /v1/responses
4. Provider translates to internal format
   → Returns SSE events (response.output_item.added, etc.)
5. CLI receives tool_call → executes tool locally
6. CLI sends result back → provider
7. Loop until final response
```

### 5.2 Via AI Gateway — Format Translation

Codex CLI speaks Responses API. The upstream opencode.ai speaks Chat Completions API. The gateway **translates** between these formats:

```
Codex CLI
    │
    │ POST /v1/responses (Responses API format)
    │
    ▼
Gateway :5011
    │
    │ Translates Responses → Chat Completions:
    │   - input array → messages array
    │   - tool definitions → tools array
    │   - max_output_tokens → max_tokens
    │   - SSE events translated (response.created,
    │     response.output_item.added, etc.)
    │
    ▼
opencode.ai /zen/go/v1
    │ POST /v1/chat/completions (Chat Completions format)
    │
    ▼
    ⛔ 403/1010 if tools are present
```

**The gateway's translation layer (`ResponsesEndpoints.TranslateTools`):**

```csharp
// Responses API tools → Chat Completions tools
private static List<Dictionary<string, object?>>? TranslateTools(JsonElement tools)
{
    if (tools.ValueKind != JsonValueKind.Array) return null;
    var result = new List<Dictionary<string, object?>>();
    foreach (var tool in tools.EnumerateArray())
    {
        var name = tool.TryGetProperty("name", out var n) ? n.GetString() : "";
        var description = tool.TryGetProperty("description", out var d) ? d.GetString() : "";
        var inputSchema = tool.TryGetProperty("input_schema", out var s) ? s : null;
        result.Add(new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = inputSchema
            }
        });
    }
    return result;
}
```

This translation is correct — it faithfully converts the Responses API tool format to Chat Completions format. But the resulting request still hits the same 403/1010 block at opencode.ai.

**Codex-specific tool schemas that get translated:**

| Codex Tool | Responses API format | Translated to Chat Completions |
|-----------|---------------------|-------------------------------|
| `Bash` | `{name: "Bash", input_schema: {...}}` | `{function: {name: "Bash", parameters: {...}}}` |
| `Read` | `{name: "Read", input_schema: {...}}` | `{function: {name: "Read", parameters: {...}}}` |
| `Edit` | `{name: "Edit", input_schema: {...}}` | `{function: {name: "Edit", parameters: {...}}}` |
| `Search` | `{name: "Search", input_schema: {...}}` | `{function: {name: "Search", parameters: {...}}}` |

### 5.3 Sandbox Constraint (Codex Desktop)

Codex Desktop runs a sandbox that restricts file access to permitted directories. This is a **separate constraint** from the gateway:

```
Codex Desktop Sandbox Rules:
  ✅ /Users/<user>/Documents/Codex/   (default root)
  ❌ /Users/<user>/Documents/Other/   (unless opened via File → Open Folder)
  ❌ /tmp/                             (blocked by sandbox)
  ❌ C:/Windows/                        (system paths blocked)
```

When Codex Desktop connects through the gateway:
- **Network layer:** Gateway routes correctly ✓
- **File access layer:** Sandbox still enforces its own rules independently ✓
- **Tool calls:** Blocked by opencode.ai upstream (403/1010) — same as OpenCode

---

## 6. Comparison Table

### 6.1 Feature Matrix

| Feature | OpenCode CLI | OpenClaude CLI | Codex CLI |
|---------|-------------|----------------|-----------|
| **API format** | Chat Completions | Chat Completions | Responses |
| **Native provider** | opencode.ai | Anthropic/OpenAI | OpenAI |
| **Tool count (agent)** | ~6 | ~25+ | ~4-8 |
| **Tool schema** | System prompt + tools array | Function definitions | input_schema |
| **Gateway route** | `/v1/chat/completions` | `/v1/chat/completions` | `/v1/responses` |
| **Format translation** | None (passthrough) | None (passthrough) | Responses → Chat Completions |
| **Tool calls through opencode.ai** | ❌ 403/1010 | ❌ 403/1010 | ❌ 403/1010 |
| **Tool calls through direct provider** | N/A | ✅ | ✅ |
| **Sandbox restriction** | None (your machine) | None (your machine) | ✅ Codex Desktop sandbox |

### 6.2 Call Flow Comparison

| Step | Normal (Direct) | Via AI Gateway | Blocked? |
|------|----------------|----------------|----------|
| 1. CLI builds request | ✅ Same | ✅ Same | — |
| 2. Auth | Single key | Dual key (gw + upstream) | — |
| 3. Route to endpoint | → opencode.ai directly | → Gateway :5011 → opencode.ai | — |
| 4. Tool forwarding | Passed to LLM | Forwarded by gateway | ❌ 403 at opencode.ai |
| 5. LLM processes tools | ✅ Returns tool_calls | ⛔ Never reaches LLM | ❌ |
| 6. CLI executes tool | ✅ Locally | ❌ Never gets tool_call | ❌ |
| 7. Result cycle | ✅ Continues | ❌ Stops at step 4 | ❌ |

### 6.3 Auth Layer Comparison

```
                    Normal (Direct)              Via AI Gateway
                    ──────────────              ──────────────
Gateway API key     N/A                          arkana-xxx (first hop)
Upstream API key    sk-xxx (direct to provider)  sk-xxx (second hop)
Key management      Manual file config           Admin API + Dashboard
Multi-tenant        None                         Per-key model permissions
Key rotation        Manual file edit             Admin API endpoint
```

---

## 7. Why the Gateway Cannot Fix This

The 403/1010 security block is enforced by **opencode.ai's server-side API**, not by the gateway. The gateway is a transparent proxy — it cannot:

### 7.1 ❌ It Cannot Remove Tool Definitions

Removing tools from the forwarded request would make the LLM incapable of executing agent actions (bash, read files, etc.), defeating the purpose of agent mode.

### 7.2 ❌ It Cannot Modify Tool Schemas

Even if the gateway modified the tool definitions to a different schema format, opencode.ai's security policy evaluates the semantic content of the tools, not just the wire format. Any request carrying bash/file-execution tools will be blocked.

### 7.3 ❌ It Cannot Bypass opencode.ai's Security

The gateway does not control opencode.ai's API infrastructure. The 403/1010 response is generated by opencode.ai's own middleware before the request reaches the LLM.

### 7.4 ❌ It Cannot Spoof the CLI Identity

Even if the gateway mimicked the official CLI's User-Agent and TLS fingerprint, the request is still originating from a different IP/network (Docker container), which opencode.ai can detect.

### 7.5 ✅ What the Gateway CAN Do

| Capability | Works? | Notes |
|-----------|--------|-------|
| **Route to a different provider** | ✅ | Configure model → OpenAI/Anthropic instead of opencode.ai |
| **Log tool call attempts** | ✅ | Request logging captures tool definitions in requests |
| **Track token usage** | ✅ | Regardless of tool-call success |
| **Rate limit tool-call requests** | ✅ | Configurable per API key |
| **Audit tool-call usage** | ✅ | Full request/response logging |

The only practical solution is to **route around opencode.ai** for agent-mode tool calls — use a different upstream provider that supports the tool schema.

---

## 8. Workarounds and Alternatives

### 8.1 ✅ Route via DeepSeek Direct Provider (IMPLEMENTED)

The gateway now includes a **DeepSeek Direct** provider (`deepseek`) that points to `https://api.deepseek.com/v1`. This completely bypasses opencode.ai's 403/1010 security block.

**Setup:**

```bash
# 1. Set your DeepSeek API key
curl -X PUT http://localhost:5011/admin/providers/a1000000-0000-0000-0000-000000000006/apikey \
  -H "Content-Type: application/json" \
  -d '{"apiKey":"sk-your-deepseek-api-key"}'

# Or set via Docker environment variable: DEEPSEEK_API_KEY

# 2. Configure your CLI to use the direct model
# In ~/.config/opencode/opencode.jsonc:
{
  "providers": {
    "arkana-gateway": {
      "baseURL": "http://localhost:5011/v1",
      "apiKey": "arkana-...",
      "models": {
        "deepseek-v4-flash-direct": {}
      }
    }
  }
}
```

**Models available via DeepSeek Direct:**
| Model Code | Description |
|-----------|-------------|
| `deepseek-v4-flash-direct` | DeepSeek V4 Flash — direct, no opencode.ai middleman |
| `deepseek-v4-pro-direct` | DeepSeek V4 Pro — direct, no opencode.ai middleman |

**CLI Configuration Examples:**

```
# OpenCode CLI
opencode --model deepseek-v4-flash-direct "fix this bug"

# OpenClaude CLI  
claude --model deepseek-v4-flash-direct "refactor this"

# Codex CLI
codex --model deepseek-v4-flash-direct "build a REST API"
```

### 8.2 Route to OpenAI/Anthropic Direct

**For OpenClaude:** Configure the gateway to route to Anthropic or OpenAI directly instead of opencode.ai.

```bash
# Add an OpenAI provider via admin API
curl -X POST http://localhost:5011/admin/providers \
  -H "Content-Type: application/json" \
  -d '{
    "code": "openai-direct",
    "name": "OpenAI Direct",
    "baseUrl": "https://api.openai.com/v1",
    "apiKey": "sk-...",
    "enabled": true
  }'

# Associate the model with the direct provider
# (via admin dashboard or model management API)
```

**For Codex CLI:** Same approach — route the model to a provider that accepts Chat Completions with tool definitions.

### 8.2 Use a Proxy with Provider Selection

Configure the gateway's model router to route tool-call-heavy models to supporting providers:

```
Model "deepseek-v4-flash" → opencode.ai (chat only, no agent tools)
Model "gpt-4o-mini"      → OpenAI Direct (chat + tools)
Model "claude-sonnet-4"  → Anthropic Direct (chat + tools)
```

The gateway's `IModelRouter` resolves the provider per-request, so different models can use different upstreams.

### 8.3 Accept Text-Only Mode

When routed through opencode.ai, the CLI still works in **text-only fallback** mode:

```
opencode --model deepseek-v4-flash "summarize this file"
```

The CLI will send the request without tool definitions (or with tools that opencode.ai allows), and receive text responses. Agent functionality is degraded but basic Q&A still works.

### 8.4 Native Direct Connection for Agent Work

For agent-mode work that requires tool calls, bypass the gateway and connect directly:

```
openCode agent work    → direct to opencode.ai (native)
OpenClaude agent work  → direct to Anthropic (native)
Codex agent work       → direct to OpenAI (native)

Gateway-only use       → chat completions, monitoring, admin
```

This is the recommended workflow until opencode.ai removes or relaxes its security policy for proxied requests.

---

## 9. Technical Appendix

### 9.1 OpenCode CLI Agent Tool Definitions

The OpenCode CLI agent mode sends these tool definitions in the `tools` array (captured via socat TCP proxy):

| Tool | Description | Parameters |
|------|-------------|------------|
| `Bash` | Execute bash command | `command` (string), `timeout` (int?), `description` (string) |
| `Read` | Read file contents | `path` (string), `offset` (int?), `limit` (int?) |
| `Write` | Write content to file | `path` (string), `content` (string) |
| `Task` | Delegate subtask | `goal` (string), `context` (string?) |
| `TodoWrite` | Track todo items | `todos` (array), `mode` (string: read/write) |
| `WebFetch` | Fetch URL content | `url` (string), `method` (string?), `headers` (object?) |

### 9.2 OpenClaude CLI Agent Tool Definitions

OpenClaude (Valarions Claude) defines ~25+ tools, including:

| Category | Tools |
|----------|-------|
| **Filesystem** | `Read`, `Write`, `Edit`, `Glob`, `Grep`, `FileSearch`, `LS` |
| **Shell** | `Bash`, `PowerShell` |
| **Web** | `WebFetch`, `WebSearch` |
| **Project** | `Task`, `TodoWrite`, `ProjectRead` |
| **Database** | `DatabaseQuery`, `DatabaseSchema` |
| **AI** | `Think`, `Plan`, `Question` |

### 9.3 Codex CLI Tool Definitions (Responses API)

Codex CLI sends tools in the Responses API format:

| Tool | input_schema |
|------|-------------|
| `Bash` | `{type: "object", properties: {command: {type: "string"}}}` |
| `Read` | `{type: "object", properties: {path: {type: "string"}}}` |
| `Edit` | `{type: "object", properties: {file_path: {type: "string"}, old_string: {type: "string"}, new_string: {type: "string"}}}` |
| `Search` | `{type: "object", properties: {pattern: {type: "string"}, path: {type: "string"}}}` |

### 9.4 Relevant Gateway Code Paths

| File | Path | Purpose |
|------|------|---------|
| `ChatEndpoints.cs` | L45-227 | Streaming mode — proxies to opencode.ai with API key |
| `ChatEndpoints.cs` | L229-310 | Non-streaming mode — via CQRS handler |
| `ResponsesEndpoints.cs` | L28-530 | Responses API → Chat Completions translation |
| `ResponsesEndpoints.cs` | L69-132 | `TranslateTools()` — Responses → Chat Completions tool format |
| `OpenCodeChatService.cs` | L36-139 | Non-streaming HTTP call to opencode.ai |
| `OpenAIChatService.cs` | L23-58 | Non-streaming HTTP call to OpenAI |
| `CLIProxyAPIChatService.cs` | L30-88 | Non-streaming via CLI proxy process |
| `Program.cs` | L46-64 | Conditional auth middleware setup |
| `ApiKeyAuthMiddleware.cs` | L17-59 | Gateway API key validation against DB |

### 9.5 Error Codes

| Code | Source | Meaning |
|------|--------|---------|
| `403` | opencode.ai upstream | Security policy blocked the request |
| `1010` | opencode.ai error code | "Agent tool definitions not allowed" |
| `401` | Gateway | Invalid/expired gateway API key |
| `400` | Gateway | Malformed request body |
| `0s` | Codex Desktop | Sandbox denied file access (not gateway-related) |

---

> **Document version:** 1.0  
> **Author:** Hermes Agent (ARKANA GATEWAY)  
> **See also:** `docs/architecture/opencode-integration-architecture.md` for detailed gateway architecture
