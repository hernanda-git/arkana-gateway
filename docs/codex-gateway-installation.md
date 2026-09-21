# Codex + ARKANA GATEWAY: Canonical Installation and Verification

> Employee-facing guide for Codex CLI and Codex Desktop.
>
> **Production status verified:** 2026-08-21
>
> This document is the canonical setup path. Older incident reports and historical adapter notes remain useful for history, but they are not the installation authority.

## 1. Verified production facts

| Item | Value |
|---|---|
| Gateway API Base URL | `https://gateway.arkana.dev/v1` |
| Health Endpoint | `https://gateway.arkana.dev/health` |
| Responses Endpoint | `POST https://gateway.arkana.dev/v1/responses` |
| Codex Provider id | `arkana-gateway` |
| Verified Codex model | `gpt-5.5` |
| Codex wire API | `responses` |
| Client credential variable | `GATEWAY_API_KEY` |
| Production upstream | ChatGPT OAuth Provider, selected by Gateway routing |
| Local adapter | Not required for the current direct HTTPS path |

The API key value is intentionally never included in this documentation.

## 2. Architecture

```text
Codex CLI / Codex Desktop
        |
        | HTTPS POST /v1/responses
        | Authorization: Bearer <Gateway API key>
        v
https://gateway.arkana.dev/v1
        |
        | API key validation, model routing, Responses translation,
        | SSE translation, Tool-loop continuation handling, Request Logs
        v
ChatGPT OAuth Provider
        |
        v
ChatGPT Codex Responses upstream
```

Codex executes local Tools on the employee machine. The Gateway transports the model Request and Response and records operational metadata. The Gateway does not execute the employee's shell command.

## 3. Prerequisites

- Codex CLI 0.149.0 or newer is recommended.
- Codex Desktop installed if the Desktop UI is required.
- Windows, macOS, or Linux with HTTPS access to `gateway.arkana.dev`.
- A Gateway API key issued by the Gateway administrator.
- For Windows Computer Use, Hermes requires `cua-driver 0.20.0` or newer. This is separate from Codex Tool execution.

Check Codex:

```bash
codex --version
```

Windows PowerShell:

```powershell
codex --version
```

## 4. Credential handling

Never put a real API key in:

- Git commits
- Screenshots
- Chat messages
- Agent prompts
- Shell command arguments
- Shared documentation
- Process command lines

Preferred approach:

### Windows

```powershell
[Environment]::SetEnvironmentVariable('GATEWAY_API_KEY', 'YOUR_GATEWAY_API_KEY', 'User')
```

Open a new terminal after changing the User Environment. Existing processes do not automatically receive the new value.

### macOS / Linux

```bash
export GATEWAY_API_KEY='YOUR_GATEWAY_API_KEY'
```

For persistence, use the user's normal shell profile or a secret manager. Do not commit the profile.

Verify presence only:

```powershell
if ([string]::IsNullOrWhiteSpace($env:GATEWAY_API_KEY)) { 'MISSING' } else { 'PRESENT' }
```

```bash
[ -n "$GATEWAY_API_KEY" ] && echo PRESENT || echo MISSING
```

## 5. Codex configuration

Codex reads:

```text
Windows: %USERPROFILE%\.codex\config.toml
macOS/Linux: ~/.codex/config.toml
```

Use this provider block. Replace only the placeholder through a secret-safe local method. Do not paste a real key into a shared document.

```toml
model = "gpt-5.5"
model_provider = "arkana-gateway"
model_catalog_json = "C:\\Users\\YOUR_USER\\.codex\\model_catalog.json"

[model_providers.arkana-gateway]
name = "ARKANA GATEWAY"
base_url = "https://gateway.arkana.dev/v1"
wire_api = "responses"
env_key = "GATEWAY_API_KEY"
# Optional GUI fallback. Keep it local and never commit it.
# experimental_bearer_token = "YOUR_GATEWAY_API_KEY"
```

On macOS/Linux, use the equivalent path:

```toml
model_catalog_json = "/Users/YOUR_USER/.codex/model_catalog.json"
```

### Why `wire_api = "responses"` is mandatory

Codex uses the Responses API. If this is omitted, the client may call `/v1/chat/completions`, which is not the canonical Codex path and can produce misleading 404 or format errors.

### Model catalog

The catalog tells Codex that the model supports direct shell Tool execution. A minimal catalog is:

```json
{
  "models": [
    {
      "slug": "gpt-5.5",
      "display_name": "GPT-5.5 (ARKANA GATEWAY)",
      "provider": "arkana-gateway",
      "name": "GPT-5.5 via ARKANA GATEWAY",
      "supported_tools": ["shell_command"],
      "experimental_supported_tools": [],
      "supports_parallel_tool_calls": true,
      "reasoning": true,
      "shell_type": "shell_command",
      "visibility": "list",
      "supported_in_api": true,
      "priority": 100,
      "base_instructions": "",
      "support_verbosity": true,
      "tool_mode": "direct",
      "truncation_policy": { "type": "auto", "mode": "tokens", "limit": 128000 },
      "supported_reasoning_levels": [
        { "level": "low", "effort": "low", "description": "Fast reasoning" },
        { "level": "medium", "effort": "medium", "description": "Balanced reasoning" },
        { "level": "high", "effort": "high", "description": "Deep reasoning" }
      ]
    }
  ]
}
```

The automated setup scripts in this repository create the correct OS-specific path.

## 6. First validation

### 6.1 Health

```bash
curl -fsS https://gateway.arkana.dev/health
```

Expected: HTTP success and a healthy status response.

### 6.2 Authentication and models

```bash
curl -fsS https://gateway.arkana.dev/v1/models \
  -H "Authorization: Bearer $GATEWAY_API_KEY" \
  | python3 -m json.tool
```

Windows PowerShell:

```powershell
Invoke-RestMethod 'https://gateway.arkana.dev/v1/models' -Headers @{ Authorization = "Bearer $env:GATEWAY_API_KEY" }
```

Do not print the Authorization header or key in a report.

### 6.3 Codex text request

```bash
codex exec --skip-git-repo-check --json 'Reply exactly CODEX_GATEWAY_TEXT_OK'
```

Expected marker:

```text
CODEX_GATEWAY_TEXT_OK
```

### 6.4 Codex real Tool-loop test

Do not accept an assistant sentence claiming that a command ran. Verify the artifact.

```bash
codex exec --skip-git-repo-check --json \
  'Use your shell Tool to write exactly CODEX_GATEWAY_TOOL_OK to /tmp/codex-gateway-proof.txt, then read it back.'
cat /tmp/codex-gateway-proof.txt
```

Windows PowerShell:

```powershell
$proof = Join-Path $env:TEMP 'codex-gateway-proof.txt'
if (Test-Path $proof) { Remove-Item $proof -Force }
codex exec --skip-git-repo-check --json "Use your shell Tool to write exactly CODEX_GATEWAY_TOOL_OK to $($proof -replace '\\','/'), then read it back."
Get-Content -LiteralPath $proof
```

Acceptance requires all three signals:

1. Codex output contains a real `command_execution` Tool event.
2. The artifact exists and contains the expected marker.
3. Gateway Logs show the corresponding `/v1/responses` Request routed to the intended Provider.

## 7. Complex Tool-loop verification

The production verification performed on 2026-08-21 used one Codex Request to execute these real operations:

1. Create a temporary directory.
2. Write valid JSON with exactly three records.
3. Read the JSON back.
4. Search for `COMPLEX_GATEWAY_MARKER` with PowerShell `Select-String`.
5. Parse JSON and count records.
6. Write a report file.
7. Read the report file back.
8. Attempt Browser Tool usage.

Actual result:

- Directory creation: passed.
- JSON write: passed.
- JSON read: passed.
- Marker search: passed.
- Record count: passed with `3`.
- Report write: passed.
- Report read: passed.
- Browser Tool: not available in the verified Codex CLI environment, so no browser result was claimed.

The verified artifact directory was:

```text
%TEMP%\\codex-complex-gateway-test
```

The test used the production custom Provider `arkana-gateway`, model `gpt-5.5`, and Gateway API authentication. The API key value was not included in the report.

## 8. Codex Desktop

Codex Desktop uses the same Codex configuration directory, but it is a long-lived packaged process. After changing the key or provider configuration:

1. Close Codex Desktop completely.
2. Confirm no packaged `OpenAI.Codex` `app-server` process remains.
3. Launch Codex Desktop again.
4. Open a workspace.
5. Run the same artifact-producing Tool test.
6. Confirm the Gateway Logs entry.

On Windows, verify the process without exposing credentials:

```powershell
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -eq 'codex.exe' -and $_.ExecutablePath -like 'C:\Program Files\WindowsApps\OpenAI.Codex*' } |
  Select-Object ProcessId, ExecutablePath, CommandLine
```

A `codex app` launcher message alone is not proof that the Desktop App is running.

## 9. Gateway Logs verification

Use the Gateway Dashboard Logs page or the production operations channel. Verify:

- Provider: `ChatGPT` or the configured Provider display name.
- Model: `gpt-5.5`.
- API key label: the employee's Gateway key label, not the key value.
- Request Path: `/v1/responses`.
- IsError: false.
- No 401, 400, or 502 for the accepted test.

The database `ToolCallsJson` field is not a sufficient sole acceptance signal for native Responses requests because some Responses Tool details are represented in the request/response JSON rather than that legacy column. Use the client Tool event plus artifact plus Gateway Request Path and Provider together.

## 10. Troubleshooting

| Symptom | Likely cause | Action |
|---|---|---|
| 401 Invalid or expired API key | Key is wrong, expired, inactive, or created on another Gateway instance | Validate `/v1/models` against the production hostname; ask the admin to confirm the key metadata. Never rotate blindly. |
| Codex calls `/v1/chat/completions` | Wrong wire mode | Set `wire_api = "responses"`. |
| Text works but Tool is absent | Stale model metadata or stale Codex process | Recreate `model_catalog.json`, remove stale model cache, restart CLI/Desktop, run artifact test. |
| Tool executes but turn two returns `No tool call found` | Old Gateway image or stateless continuation bug | Redeploy the current Gateway image and retest. Do not use a synthetic success message as proof. |
| `Reconnecting 5/5` | Upstream response parsing, SSE, auth, rate limit, or old image | Check exact Gateway Logs before changing config. |
| Browser unavailable | Client did not expose a Browser Tool | Report it as unavailable. Do not claim a browser page was opened. |
| Computer Use requires cua-driver 0.20.0 | Hermes runtime selected an old binary | Install/repair Computer Use, set `HERMES_CUA_DRIVER_CMD`, and start a fresh Hermes runtime. |

## 11. Security rules

- Do not request an employee to paste a key into a public chat.
- Do not print a key while debugging.
- Do not place keys in Git, screenshots, issue comments, or agent-generated reports.
- Use one Gateway key per employee or Agent where practical.
- Revoke a key when the device or employee access changes.
- Treat any credential found in old material as compromised and rotate it.
