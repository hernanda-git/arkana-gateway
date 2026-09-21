# Phase 3 — Online AI Integration

## Goal
Connect all major AI providers with consistent abstractions and failover.

## Provider Integration Details

| Provider | SDK/NuGet | Auth Method | Priority |
|----------|-----------|-------------|----------|
| **OpenCode** | Custom `HttpClient` via `Microsoft.Extensions.AI` | API Key / Internal | P0 |
| **OpenAI** | `Microsoft.Extensions.AI.OpenAI` | API Key | P1 |
| **Azure OpenAI** | `Azure.AI.OpenAI` | RBAC / Key | P1 |
| **Google Gemini** | `Microsoft.Extensions.AI.Google` | API Key / OAuth | P1 |
| **Anthropic Claude** | Manual REST via `Microsoft.Extensions.AI` | API Key | P2 |
| **Ollama** | `Microsoft.Extensions.AI.Ollama` | None (local) | P2 |

## Architecture

```csharp
// Provider chain with OpenCode as primary, OpenAI as fallback
services.AddChatClient(builder => builder
    .UseOpenTelemetry()
    .UseRateLimiting()
    .UseFunctionInvocation()
    // Primary: OpenCode (internal, low-cost)
    .Use(new HttpClient().AsChatClient("opencode"))
    // Fallback: OpenAI / Anthropic
    .UseFallback(fallback => fallback
        .Use(new OpenAIClient(key).AsChatClient("gpt-4o"))
        .Use(new AnthropicClient(antKey).AsChatClient("claude-sonnet-4"))));
```

## Tasks

- [ ] OpenCode connector (internal API proxy, OpenAI-compatible)
- [ ] OpenAI connector with streaming support
- [ ] Azure OpenAI connector (managed identity support)
- [ ] Google Gemini connector
- [ ] Anthropic Claude connector
- [ ] Ollama connector (local model support)
- [ ] Provider health check endpoints
- [ ] Automatic failover between providers
- [ ] Provider latency/error metrics