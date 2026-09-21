# Copy-Paste Prompt: Ask an Agent to Install ARKANA GATEWAY Provider

Use this prompt in a coding Agent on an employee machine. The Agent must execute the setup, not merely describe it.

```text
You are setting up ARKANA GATEWAY as a custom Provider for this machine.

Authoritative production Endpoint:
https://gateway.arkana.dev/v1

Provider id:
arkana-gateway

Verified Codex model:
gpt-5.5

Rules:
1. Inspect the OS, installed Agent names/versions, and existing config paths first.
2. Do not modify unrelated projects, repositories, source code, or existing Provider blocks.
3. Do not print, echo, log, screenshot, commit, or include any API key/token/password in your report.
4. Never put a credential in a command-line argument if a secure prompt or environment-variable API is available.
5. If GATEWAY_API_KEY is missing, ask the human to provide it through the Agent's secure secret mechanism. Never ask them to paste it into a public issue, repository, or shared document.
6. Preserve a timestamped local backup of every config file before editing it.
7. Configure only the Agent that the human requested. If multiple Agents are requested, handle them one at a time and verify each separately.
8. For Codex CLI/Desktop:
   - configure model = "gpt-5.5"
   - configure model_provider = "arkana-gateway"
   - configure wire_api = "responses"
   - configure base_url = "https://gateway.arkana.dev/v1"
   - configure env_key = "GATEWAY_API_KEY"
   - create a local model catalog that declares shell_type = "shell_command" and tool_mode = "direct"
   - do not use localhost:8892 or an adapter unless the human explicitly asks for legacy compatibility
9. For OpenCode:
   - configure Provider id arkana-gateway
   - configure Base URL https://gateway.arkana.dev/v1
   - use the installed OpenCode schema, not a guessed schema
10. For other Agents:
   - first confirm that the installed version supports a custom OpenAI-compatible Endpoint
   - configure the Gateway only if the protocol matches
   - if the Agent is Anthropic-native, Gemini-native, or product-locked, say that direct setup is not verified instead of guessing
11. Verify in this order:
   - health request to /health
   - authenticated /v1/models request, checking only status and model presence
   - text request with a unique marker
   - real Tool request that writes a temporary artifact
   - independent readback of that artifact
   - Gateway Logs check for Provider, Model, Path, IsError, and any 4xx/5xx
12. A sentence saying “I ran the command” is not proof. Require a real Tool event and an artifact readback.
13. Browser Tool is optional. Attempt it only if the client exposes a real Browser Tool. If unavailable, report “Browser Tool not available” and do not fabricate a page title.
14. At the end, report:
   - Agent and version
   - config path changed
   - Provider id
   - Base URL
   - model
   - wire/API mode
   - credential source, only as “environment variable present” or “missing”
   - text verification result
   - Tool verification result
   - artifact path and marker, if safe
   - Gateway Log verification result
   - files backed up
   - any remaining limitation
15. Do not report the API key value.

Start by inspecting the machine and current Agent configuration. Then perform the setup and verification autonomously.
```

## Human handoff

The human must still obtain a valid Gateway API key from the Gateway administrator. The setup Agent must not invent a key, reuse a credential found in an old file, or copy a key from another employee.
