# ARKANA GATEWAY Agent Compatibility Matrix

> This matrix separates **verified**, **conditional**, and **not direct** integrations. It is intentionally conservative. An Agent is not marked verified because a config file looks plausible; it needs a real Request and, for coding Agents, an artifact-producing Tool test.

## Production connection

```text
https://gateway.arkana.dev/v1
```

Authentication:

```text
Authorization: Bearer <Gateway API key>
```

## Matrix

| Agent | Direct Gateway path | Tool execution | Status | Notes |
|---|---|---:|---|---|
| Codex CLI | Responses API, `wire_api = "responses"` | Shell Tool verified | **Verified** | Use Provider id `arkana-gateway`, model `gpt-5.5`, and the model catalog. |
| Codex Desktop | Same shared Codex config | Shell Tool expected | **Verified process/config; verify per device** | Restart the packaged App after config changes and perform the artifact test. |
| OpenCode CLI | OpenAI-compatible `/v1` | Tool loop verified | **Verified** | Use `arkana-gateway/gpt-5.5` or an allowed model returned by `/v1/models`. |
| Cline | OpenAI-compatible Chat Completions | Conditional | **Conditional** | Configure its OpenAI-compatible Provider and test the actual Tool event. |
| Roo Code | OpenAI-compatible Chat Completions | Conditional | **Conditional** | Configure the custom OpenAI-compatible Endpoint and run an artifact test. |
| Continue | OpenAI-compatible Chat Completions | Conditional | **Conditional** | Use the model/provider config supported by the installed Continue version. |
| Aider | OpenAI-compatible Chat Completions | Conditional | **Conditional** | Use the Gateway Base URL and model flags; verify file edit and test execution. |
| Kilo Code | OpenAI-compatible client | Conditional | **Conditional** | Confirm the installed version's custom Endpoint fields before setup. |
| Zero | OpenAI-compatible client | Conditional | **Conditional** | Validate whether the version sends the key in the Authorization header. |
| Python OpenAI SDK | Chat Completions or Responses | Conditional | **Conditional** | Use the SDK method matching the chosen Endpoint. |
| Node OpenAI SDK | Chat Completions or Responses | Conditional | **Conditional** | Use the SDK method matching the chosen Endpoint. |
| LiteLLM | OpenAI-compatible proxy/client | Conditional | **Conditional** | Configure Gateway as the upstream and verify auth/header passthrough. |
| Claude Code | Anthropic-native protocol | No direct claim | **Not direct** | The Gateway's documented canonical path is OpenAI-compatible. Use an explicit Anthropic-compatible bridge only if separately deployed and verified. |
| Gemini CLI | Gemini-native protocol | No direct claim | **Not direct** | Do not point it at the OpenAI-compatible Endpoint unless the installed version explicitly supports it. |
| Cursor | Product-specific | No direct claim | **Not direct** | Custom Endpoint support and policy vary by Cursor version and plan. |

## Standard verification for conditional Agents

1. Confirm the Agent's actual config path and custom Endpoint support.
2. Set Base URL to `https://gateway.arkana.dev/v1`.
3. Set the API key through the Agent's secret store or environment variable.
4. Query `/v1/models` without printing the key.
5. Run a text request with a unique marker.
6. Run a Tool request that writes an artifact.
7. Read the artifact independently outside the Agent.
8. Check Gateway Logs for Provider, Model, Path, status, and errors.
9. Mark the Agent verified only when all signals agree.

## Common config shapes

### OpenCode

```jsonc
{
  "provider": {
    "arkana-gateway": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "ARKANA GATEWAY",
      "options": {
        "baseURL": "https://gateway.arkana.dev/v1",
        "apiKey": "YOUR_GATEWAY_API_KEY"
      }
    }
  }
}
```

Use the actual schema key supported by the installed OpenCode version. The production verification used the `arkana-gateway` Provider and model `gpt-5.5`.

### Generic OpenAI SDK

```python
from openai import OpenAI

client = OpenAI(
    api_key=os.environ["GATEWAY_API_KEY"],
    base_url="https://gateway.arkana.dev/v1",
)
response = client.responses.create(
    model="gpt-5.5",
    input="Reply exactly SDK_GATEWAY_OK",
)
print(response.output_text)
```

Use `chat.completions.create` only when the client is configured for Chat Completions. Codex must use Responses.

## Browser Tool rule

Browser support is an Agent capability, not a Gateway guarantee. The Gateway can transport model requests, but it does not create a Browser Tool inside a client that does not expose one. If the Agent says Browser Tool is unavailable, record that result honestly.
