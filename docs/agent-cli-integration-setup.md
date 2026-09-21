# AI Gateway — Agent CLI Integration: Step-by-Step Setup Guide

> **Production correction:** start with [`codex-gateway-installation.md`](codex-gateway-installation.md), [`agent-gateway-compatibility.md`](agent-gateway-compatibility.md), and [`agent-setup-prompt.md`](agent-setup-prompt.md). This side-by-side guide contains historical local-development examples; replace local `http://localhost:5011/v1` with production `https://gateway.arkana.dev/v1` when configuring an employee machine.

> **Connect OpenCode CLI, OpenClaude CLI, and Codex CLI to the ARKANA GATEWAY.**
> **Every command is copy-paste ready. No AI agent needed.**
>
> 📌 **Setting up Codex from zero?** Read **[`docs/codex-setup-from-scratch.md`](codex-setup-from-scratch.md)**
> first — it's the end-to-end gateway + Codex walkthrough (clone → env → stack →
> migrations → key → Codex config → tool-loop verification). This file covers all
> three CLIs side by side.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Gateway Setup](#2-gateway-setup)
3. [Create an API Key](#3-create-an-api-key)
4. [OpenCode CLI Setup](#4-opencode-cli-setup)
5. [OpenClaude CLI Setup](#5-openclaude-cli-setup)
6. [Codex CLI Setup](#6-codex-cli-setup)
7. [Testing Your Setup](#7-testing-your-setup)
8. [Troubleshooting](#8-troubleshooting)

---

## 1. Prerequisites

Before starting, ensure you have:

| Tool | Minimum Version | Check Command |
|------|----------------|---------------|
| Docker Desktop | 4.x (with WSL2) | `docker --version` |
| .NET SDK | 10.0 | `dotnet --version` |
| Git | 2.x | `git --version` |
| curl | 7.x | `curl --version` |

**Open a terminal** (WSL, PowerShell, or Command Prompt — all commands work the same).

---

## 2. Gateway Setup

### 2.1 Clone the Repository

```bash
cd ~
git clone https://github.com/hernanda-git/arkana-gateway.git
cd gateway
```

### 2.2 Set the Upstream API Key

The Gateway needs your OpenCode Go API key to proxy requests to the LLM. Set it as an environment variable:

```bash
# Replace sk-your-actual-key with your real OpenCode Go API key
export OPENCODE_GO_API_KEY="sk-your-actual-key"
```

For Docker, pass it through the compose file or `.env`. Create a `.env` file in the `deploy/` directory:

```bash
cd deploy
cat > .env << 'EOF'
OPENCODE_GO_API_KEY=sk-your-actual-key-here
N8N_ENCRYPTION_KEY=$(openssl rand -hex 32)
N8N_EMAIL=admin@example.com
N8N_PASSWORD=admin123
EOF
cd ..
```

### 2.3 Start the Gateway

```bash
# Start all services (PostgreSQL, Redis, n8n, Gateway)
cd deploy
docker compose up -d
cd ..
```

Wait 15 seconds for services to be ready. Check they're running:

```bash
docker ps --filter "name=arkana-*" --format "table {{.Names}}\t{{.Status}}"
```

You should see four containers with `Up` status:

```
NAMES                 STATUS
arkana-gateway     Up X seconds (healthy)
arkana-postgres    Up X seconds (healthy)
arkana-redis       Up X seconds (healthy)
arkana-n8n         Up X seconds
```

### 2.4 Apply Database Migrations

```bash
dotnet ef database update \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api
```

### 2.5 Verify the Gateway

```bash
# Dashboard should be accessible
curl -s -o /dev/null -w "%{http_code}" http://localhost:5011/
# Expected output: 200

# Models endpoint (no auth needed)
curl -s http://localhost:5011/v1/models | python3 -m json.tool | head -20
# You should see available models like deepseek-v4-flash, deepseek-v4-pro, etc.
```

---

## 3. Create an API Key

The Gateway uses API keys to authenticate agents. Each agent gets its own key.

### Option A: Via the Dashboard (GUI)

1. Open **http://localhost:5011/** in your browser
2. Click **API Keys** in the sidebar
3. Click **Create Key**
4. Fill in:
   - **Name**: Give it a descriptive name (e.g., "OpenCode CLI", "Codex Desktop")
   - **Allowed Models**: Leave empty to allow all models, or select specific ones
5. Click **Create**
6. **Copy the key immediately** — it's shown only once!

### Option B: Via the Admin API (CLI)

```bash
# Create a key named "My Agent CLI"
curl -s -X POST http://localhost:5011/admin/api-keys \
  -H "Content-Type: application/json" \
  -d '{"name":"My Agent CLI","allowedModelIds":[]}' \
  | python3 -m json.tool
```

**Output example:**
```json
{
    "id": "a1b2c3d4-...",
    "name": "My Agent CLI",
    "plainTextKey": "YOUR_GATEWAY_API_KEY",
    "message": "Save this key securely — it will not be shown again."
}
```

> ⚠️ **SAVE YOUR KEY NOW.** The `plainTextKey` is shown exactly once. Copy it to a safe place. You will use it in every agent configuration below.

**Save your key as an environment variable** (optional but recommended):

```bash
export GATEWAY_API_KEY="YOUR_GATEWAY_API_KEY"
```

---

## 4. OpenCode CLI Setup

OpenCode CLI is a terminal-based AI coding agent. It supports multiple AI providers and connects to any OpenAI-compatible API.

### 4.1 Install OpenCode CLI

```bash
npm install -g opencode
```

Verify installation:

```bash
opencode --version
# Expected: 1.15.x or newer
```

### 4.2 Configure the Gateway Provider

OpenCode CLI stores its configuration in `~/.config/opencode/opencode.jsonc`.

**Create or edit the config file:**

```bash
# Create the config directory if it doesn't exist
mkdir -p ~/.config/opencode

# Edit the config file with your preferred editor
# (use nano, vim, code, or notepad depending on your OS)

# Linux/WSL:
nano ~/.config/opencode/opencode.jsonc

# Windows (PowerShell):
# notepad "$env:USERPROFILE\.config\opencode\opencode.jsonc"
```

**Paste this exact content** (replace `YOUR_API_KEY` with the key you saved in Step 3):

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "plugin": [
    "oh-my-openagent"
  ],
  "provider": {
    "arkana-gateway": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "ARKANA GATEWAY",
      "options": {
        "baseURL": "http://localhost:5011/v1",
        "apiKey": "YOUR_API_KEY"
      }
    }
  }
}
```

> ⚠️ **Replace `YOUR_API_KEY`** with the actual key. Example: `"apiKey": "YOUR_GATEWAY_API_KEY"`

### 4.3 Verify the Configuration

```bash
# List available models through the gateway
opencode models arkana-gateway
```

You should see models like:
```
deepseek-v4-flash
deepseek-v4-pro
glm-5.1
kimi-k2.5
...
```

### 4.4 Test with a Simple Command

```bash
# Run a simple test — should respond with "Hello World"
opencode run --model arkana-gateway/deepseek-v4-flash "echo hello world"
```

**Expected output:**
```
> Sisyphus - ultraworker · deepseek-v4-flash
$ echo hello world
hello world
```

### 4.5 Test with File Reading (Tool Calls)

```bash
# Test that tool calls work (Read tool)
opencode run --model arkana-gateway/deepseek-v4-flash "Read the file README.md and tell me the project name"
```

**Expected output** (you'll see the `→ Read` tool being used):
```
> Sisyphus - ultraworker · deepseek-v4-flash
→ Read README.md
The project is ARKANA GATEWAY...
```

### 4.6 Set as Default (Optional)

To avoid typing `arkana-gateway/` every time, make the gateway your default:

```bash
# Use the gateway as the default provider
opencode providers default arkana-gateway
```

Then you can run commands without the provider prefix:

```bash
opencode run "list files and tell me what kind of project this is"
```

---

## 5. OpenClaude CLI Setup

OpenClaude CLI (Valarions Claude) is a Claude-based AI coding agent.

### 5.1 Install OpenClaude CLI

```bash
pip install openclaude-cli
```

Verify:

```bash
openclaude --version
```

### 5.2 Configure the Gateway

OpenClaude reads providers from `~/.openclaude/config.yaml`:

```bash
mkdir -p ~/.openclaude

cat > ~/.openclaude/config.yaml << 'EOF'
providers:
  arkana-gateway:
    type: openai-compatible
    base_url: http://localhost:5011/v1
    api_key: YOUR_API_KEY
    default_model: deepseek-v4-flash
EOF
```

> ⚠️ **Replace `YOUR_API_KEY`** with your actual gateway API key.

### 5.3 Test

```bash
openclaude --provider arkana-gateway --model deepseek-v4-flash "list files in current directory"
```

---

## 6. Codex CLI Setup

Codex (by OpenAI) speaks **only the OpenAI Responses API**, not Chat Completions.
The Gateway has a **native `/v1/responses` endpoint** (`ResponsesEndpoints.cs`)
that translates Responses ↔ Chat Completions server-side — so Codex points
straight at the gateway. **No client-side adapter or proxy is required.**
The earlier `arkana_gw_adapter.js` (`:8892`) was **retired 2026-07-21**.

> For the full, verified walkthrough (incl. the multi-turn tool-loop smoke test and
> LAN/remote scenarios), see **`docs/codex-setup-from-scratch.md`**. The steps below
> are the condensed version.

### 6.1 Install Codex CLI

```bash
npm install -g @openai/codex
codex --version
```

### 6.2 Configure the Gateway

Codex reads **`~/.codex/config.toml`** and uses the **responses** wire API. Point
its `model_provider` at the gateway's `/v1` base (Codex appends `/responses`):

```toml
# ~/.codex/config.toml
[model_providers.arkana-gateway]
name     = "ARKANA GATEWAY"
base_url = "http://localhost:5011/v1"   # Codex appends /responses
wire_api = "responses"                  # required: use the Responses API
experimental_bearer_token = "YOUR_API_KEY"   # gateway key from Step 3
# (or: env_key = "GATEWAY_API_KEY")

# default so `codex` just works
model_provider = "arkana-gateway"
model          = "deepseek-v4-flash"
```

> ⚠️ **Replace `YOUR_API_KEY`** with your actual gateway API key (Step 3). The
> Codex field is `experimental_bearer_token` (or `env_key`), **not** `api_key`.
> The gateway **remaps unknown model names** (e.g. Codex-internal `gpt-5.6-luna`)
> to `deepseek-v4-flash` automatically, so a missing model name won't 401 mid-turn.

### 6.3 Test

```bash
# Simple prompt
codex "list files in the current directory"

# Real tool-loop smoke test (this is what used to break — must NOT show Reconnecting 5/5)
codex
> make a folder C:\temp\codextest and write hello.txt into it
```

### 6.4 Verify the Responses endpoint directly

```bash
curl -s -N -X POST http://localhost:5011/v1/responses \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4-flash","input":"say hi in one word","stream":true}' \
  | grep -m1 'response.completed' && echo "RESPONSES_OK"
```

---

## 7. Testing Your Setup

Run these tests in order to verify everything works:

### 7.1 Simple Chat (No Tools)

```bash
# OpenCode CLI
opencode run --model arkana-gateway/deepseek-v4-flash "say hello in one word"
```

**Expected:** The agent responds with a single word greeting.

### 7.2 Tool Call: Read File

```bash
# OpenCode CLI — should use Read tool
opencode run --model arkana-gateway/deepseek-v4-flash \
  "Read the README.md file and summarize the project in 1 sentence"
```

**Expected:** You see `→ Read README.md` (tool execution) followed by a summary.

### 7.3 Tool Call: Bash Command

```bash
# OpenCode CLI — should use Bash tool
opencode run --model arkana-gateway/deepseek-v4-flash \
  "Run 'ls -la' and tell me how many files are in the current directory"
```

**Expected:** You see `$ ls -la` (bash execution) followed by file count.

### 7.4 Multi-Tool Workflow

```bash
# OpenCode CLI — create a file, then read it back
cd /tmp
opencode run --model arkana-gateway/deepseek-v4-flash \
  "Create a file test.txt with content 'Gateway works!' then read it back to verify"
```

**Expected:** You see:
- `← Write test.txt` — file created
- `Wrote file successfully`
- `→ Read test.txt` — file read back
- `Gateway works!` — verification

### 7.5 Verify via Gateway Dashboard

Open **http://localhost:5011/** in your browser and check:
- **Dashboard** — should show recent requests
- **Logs** — should show your test requests with token counts

### 7.6 Check API Key Usage

```bash
# List all API keys and check your key's activity
curl -s http://localhost:5011/admin/api-keys | python3 -m json.tool

# View recent usage logs
curl -s "http://localhost:5011/admin/logs?count=10" | python3 -m json.tool
```

---

## 8. Troubleshooting

### 8.1 "Invalid or expired API key" (401)

**Cause:** The API key is wrong or not registered in the gateway.

**Fix:**
```bash
# 1. Check your key exists
curl -s http://localhost:5011/admin/api-keys | python3 -c "
import sys, json
keys = json.load(sys.stdin)
for k in keys:
    print(f'{k[\"name\"]:20s} | active={k[\"isActive\"]} | prefix={k.get(\"prefix\",\"?\")}')
"

# 2. If your key isn't listed, create a new one (see Step 3)
# 3. Update your agent config with the new key
```

### 8.2 "Connection refused" or "Could not resolve host"

**Cause:** The Gateway is not running.

**Fix:**
```bash
# Check if containers are running
docker ps --filter "name=arkana-*"

# If not running, start them
cd ~/gateway/deploy
docker compose up -d

# Wait 15 seconds, then verify
curl http://localhost:5011/
```

### 8.3 "An error occurred while sending the request" (from CLI)

**Cause:** Intermittent connection issue — the upstream server may have timed out during large request uploads.

**Fix:** Simply retry the command. This is a transient issue that resolves on retry.

```bash
# If it fails, just run the same command again
opencode run --model arkana-gateway/deepseek-v4-flash "your prompt"
```

### 8.4 Tool calls not working (model responds with text instead of tools)

**Cause:** The model may not be receiving tool definitions correctly.

**Fix:**
```bash
# 1. Verify the model is available
curl -s http://localhost:5011/v1/models | python3 -c "
import sys, json
for m in json.load(sys.stdin)['data']:
    print(f'{m[\"id\"]:30s} | {m[\"owned_by\"]}')
"

# 2. Test with a simple tool call via curl
curl -s -X POST http://localhost:5011/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -d '{
    "model":"deepseek-v4-flash",
    "messages":[{"role":"user","content":"What is 2+2? Use the calculator."}],
    "tools":[{"type":"function","function":{"name":"calculate","description":"Calculate math","parameters":{"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"]}}}],
    "stream":false
  }' | python3 -m json.tool
```

### 8.5 OpenCode CLI shows "ProviderModelNotFoundError"

**Cause:** Wrong model name format.

**Fix:**
```bash
# Use the exact format: provider-name/model-name
opencode run --model arkana-gateway/deepseek-v4-flash "hello"

# NOT: arkana-gateway/deepseek-v4-flash-prd (wrong model name)
# NOT: deepseek-v4-flash (missing provider prefix)
```

### 8.6 Gateway Dashboard not loading

**Cause:** Blazor circuit not established.

**Fix:**
1. Hard refresh the browser (Ctrl+Shift+R)
2. Check browser console for errors (F12 → Console)
3. Restart the gateway container:
   ```bash
   cd ~/gateway/deploy
   docker compose restart gateway
   ```

---

## Quick Reference Card

```
┌─────────────────────────────────────────────────────────────┐
│                    AI Gateway Quick Reference                │
├─────────────────────────────────────────────────────────────┤
│ Dashboard:       http://localhost:5011/                      │
│ Models API:      GET  http://localhost:5011/v1/models        │
│ Chat API:        POST http://localhost:5011/v1/chat/completions │
│ Admin API:       http://localhost:5011/admin/*               │
│                                                              │
│ Default model:   deepseek-v4-flash                           │
│ Auth header:     Authorization: Bearer <api-key>             │
│                  or X-Api-Key: <api-key>                     │
└─────────────────────────────────────────────────────────────┘

OpenCode CLI config:  ~/.config/opencode/opencode.jsonc
OpenClaude CLI config: ~/.openclaude/config.yaml
Codex CLI config:     ~/.codex/config.toml   (wire_api = "responses" → /v1/responses)

Gateway logs:         docker logs arkana-gateway -f
Restart gateway:      cd ~/gateway/deploy && docker compose restart gateway
Rebuild gateway:      cd ~/gateway/deploy && docker compose build gateway && docker compose up -d gateway
```

---

> **Document version:** 2.1  
> **Last updated:** 2026-07-19  
> **Verified with:** OpenCode CLI 1.15.13, Codex CLI (Responses API), Gateway main  
> **Windows path:** `C:\Workspace\gateway\docs\agent-cli-integration-setup.md`
