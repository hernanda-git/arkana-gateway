# ARKANA GATEWAY — Full Project Documentation

> **Comprehensive end-to-end reference:** architecture, code structure, every component, data flow, and CLI integration.

> **Last updated:** 2026-08-26  
> **Version:** main@f3ab0ea (final reconciliation — single-branch)  
> **Runtime:** .NET 10 + Blazor Server + PostgreSQL 16

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Directory Structure](#2-directory-structure)
3. [Solution Architecture (6-Layer Clean Architecture)](#3-solution-architecture-6-layer-clean-architecture)
4. [Domain Layer](#4-domain-layer)
5. [Application Layer (CQRS)](#5-application-layer-cqrs)
6. [Infrastructure Layer](#6-infrastructure-layer)
7. [Gateway API Layer](#7-gateway-api-layer)
8. [Service Defaults & AppHost](#8-service-defaults--apphost)
9. [Blazor Dashboard](#9-blazor-dashboard)
10. [Docker Infrastructure](#10-docker-infrastructure)
11. [Middleware Pipeline](#11-middleware-pipeline)
12. [Authentication & Authorization](#12-authentication--authorization)
13. [Request Flow: End to End](#13-request-flow-end-to-end)
14. [AI Provider Integration](#14-ai-provider-integration)
15. [CLI Integrations](#15-cli-integrations)
16. [Tool Calling Architecture](#16-tool-calling-architecture)
17. [Database Schema & Migrations](#17-database-schema--migrations)
18. [Configuration Reference](#18-configuration-reference)
19. [Admin API Reference](#19-admin-api-reference)
20. [Development Guide](#20-development-guide)
21. [Deployment Guide](#21-deployment-guide)
22. [Troubleshooting](#22-troubleshooting)
23. [Appendix: All Code Files & Their Roles](#23-appendix-all-code-files--their-roles)

---

## 1. Project Overview

**ARKANA GATEWAY** is an enterprise-grade AI proxy that sits between AI agent CLIs (OpenCode, OpenClaude, Codex) and upstream LLM providers. It provides:

- **Authentication** — API key management with per-model permissions
- **Routing** — Intelligent provider selection with configurable failover
- **Observability** — Token usage tracking, cost calculation, full request/response logging
- **Administration** — Blazor Server dashboard, REST admin API, provider management
- **Format Translation** — Responses API ↔ Chat Completions API conversion (for Codex CLI)
- **Workflow Automation** — n8n integration for event-driven workflows

### 1.1 Technology Stack

| Layer | Technology |
|-------|-----------|
| **Runtime** | .NET 10 |
| **Frontend** | Blazor Server (Interactive Server rendering) |
| **Database** | PostgreSQL 16 (via Npgsql + EF Core) |
| **Cache** | Redis 7 |
| **ORM** | Entity Framework Core 10 |
| **CQRS** | MediatR (commands, queries, handlers) |
| **API Format** | Minimal APIs (.NET 10) |
| **AI Protocol** | OpenAI-compatible Chat Completions API |
| **Workflow** | n8n (Docker container) |
| **Containerization** | Docker + Docker Compose |
| **Solution Format** | `.slnx` (new .NET 10 format) |

### 1.2 Core Design Principles

1. **API keys in database, not configuration** — upstream provider keys stored in `AiProviders` table, fetched at runtime by each service. Env var is a last-resort bootstrap fallback.
2. **Gateway is transparent to model parameters** — temperature, max_tokens, and other inference parameters are NOT gateway concerns. They are NEVER decomposed into gateway DTOs. The upstream provider applies its own defaults.
3. **Tool definitions pass through faithfully** — the gateway forwards tool definitions byte-for-byte from client to upstream (with format translation when needed for Responses API).
4. **Two-layer auth** — gateway API key (layer 1) validates the client; upstream API key (layer 2) authenticates to the provider. Layers are independent.

---

## 2. Directory Structure

```
ai-gateway/
│
├── Arkana.slnx                          # Solution file (.slnx format)
├── Directory.Build.props                   # Shared MSBuild properties
├── README.md                              # Quick-start guide
├── CLAUDE.md                              # AI assistant instructions
├── Dockerfile                             # Multi-stage build Dockerfile
│
├── deploy/
│   ├── docker-compose.yml                 # PostgreSQL + Redis + n8n + Gateway
│   └── .env                               # Environment secrets (gitignored)
│
├── docs/
│   ├── tool-calls-and-ai-gateway.md       # Tool calling analysis
│   └── architecture/
│       ├── opencode-integration-architecture.md   # OpenCode integration detail
│       └── opencode-integration-architecture.html # HTML companion
│
├── src/
│   ├── Arkana.Domain/                  # Core domain layer
│   ├── Arkana.Application/             # CQRS application layer
│   ├── Arkana.Infrastructure/          # EF Core, AI services, repos
│   ├── Arkana.Gateway.Api/             # Blazor dashboard + REST endpoints
│   ├── Arkana.ServiceDefaults/         # Shared service config
│   └── Arkana.AppHost/                 # .NET Aspire orchestration
│
└── tests/
    ├── Arkana.Domain.Tests/
    ├── Arkana.Application.Tests/
    ├── Arkana.Infrastructure.Tests/
    ├── Arkana.Gateway.Api.Tests/
    └── Arkana.ServiceDefaults.Tests/
```

---

## 3. Solution Architecture (6-Layer Clean Architecture)

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Arkana.Gateway.Api                            │
│  (Blazor Dashboard + Minimal API Endpoints + Middleware)            │
│                                                                     │
│  ┌──────────────────────┐  ┌──────────────────────────────────┐    │
│  │  Blazor Components    │  │  Endpoints                       │    │
│  │  (Pages, Layout,     │  │  - ChatEndpoints                 │    │
│  │   Shared)            │  │  - ResponsesEndpoints            │    │
│  │                      │  │  - AdminEndpoints                │    │
│  └──────────────────────┘  └──────────────────────────────────┘    │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │  Middleware Pipeline                                        │    │
│  │  - ApiKeyAuthMiddleware (conditional on path)               │    │
│  │  - TokenTrackingMiddleware (all requests)                   │    │
│  └────────────────────────────────────────────────────────────┘    │
└──────────────────────────┬─────────────────────────────────────────┘
                           │ MediatR / DI
┌──────────────────────────▼─────────────────────────────────────────┐
│                    Arkana.Application                           │
│  (CQRS Commands, Queries, Handlers, Validators)                    │
│                                                                     │
│  - SendChatCommand / SendChatHandler (chat + tool routing)          │
│  - GetTokenUsageQuery / GetTokenUsageHandler                       │
│  - CreateApiKeyCommand                                             │
│  - SendChatCommandValidator                                        │
└──────────────────────────┬─────────────────────────────────────────┘
                           │ DI
┌──────────────────────────▼─────────────────────────────────────────┐
│                    Arkana.Infrastructure                        │
│  (EF Core, AI Provider Services, Repositories, Migrations)         │
│                                                                     │
│  ┌────────────────────┐  ┌──────────────────────────────────┐      │
│  │  AI Services        │  │  Persistence                     │      │
│  │  - OpenCodeChat     │  │  - GatewayDbContext              │      │
│  │  - OpenAIChat       │  │  - Repositories (x3)             │      │
│  │  - CLIProxyAPI      │  │  - Migrations (x4)               │      │
│  │  - ModelRouter      │  │  - Entities                     │      │
│  └────────────────────┘  └──────────────────────────────────┘      │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │  Services                                                   │    │
│  │  - EfCoreTokenTracker / InMemoryTokenTracker                │    │
│  │  - EfCoreRequestLogger                                     │    │
│  │  - ApiKeyBootstrapService (IHostedService)                  │    │
│  │  - N8nService (workflow integration)                        │    │
│  └────────────────────────────────────────────────────────────┘    │
└──────────────────────────┬─────────────────────────────────────────┘
                           │ DI
┌──────────────────────────▼─────────────────────────────────────────┐
│                    Arkana.Domain                                │
│  (Entities, Interfaces, Value Objects, Domain Services)            │
│                                                                     │
│  ┌────────────────────┐   ┌──────────────────────────────────┐     │
│  │  Entities           │   │  Interfaces                     │     │
│  │  - AiProvider       │   │  - IAiProviderRepository        │     │
│  │  - ApiKey           │   │  - IApiKeyRepository            │     │
│  │  - Model            │   │  - IModelRepository             │     │
│  └────────────────────┘   │  - IChatCompletionService        │     │
│                           │  - IModelRouter                  │     │
│  ┌────────────────────┐   │  - ITokenTracker                 │     │
│  │  Value Objects      │   │  - IRequestLogger               │     │
│  │  - TokenUsage       │   │  - IN8nService                  │     │
│  │  - RequestLog       │   └──────────────────────────────────┘     │
│  │  - ToolCallInfo     │                                            │
│  └────────────────────┘   ┌──────────────────────────────────┐     │
│  ┌────────────────────┐   │  Domain Services                 │     │
│  │  Models             │   │  - ApiKeyHasher (SHA-256)       │     │
│  │  - WorkflowInfo     │   └──────────────────────────────────┘     │
│  └────────────────────┘                                            │
└────────────────────────────────────────────────────────────────────┘
```

### 3.1 Layer Dependencies

```
Gateway.Api  ──▶  Application  ──▶  Domain
     │                                  ▲
     └────▶  Infrastructure ────────────┘
     (DI registration, EF Core, AI clients)
```

Each layer depends only on the layers below it. `Gateway.Api` references both `Application` (for CQRS) and `Infrastructure` (for DI registration). `Infrastructure` references `Domain` for interfaces and entities.

---

## 4. Domain Layer

**Project:** `src/Arkana.Domain/`  
**Purpose:** Core business entities, value objects, repository interfaces, and domain services. Has zero external dependencies.

### 4.1 Entities

#### `AiProvider` — `src/Arkana.Domain/Entities/AiProvider.cs`

```csharp
public sealed class AiProvider
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }       // "OpenCode", "OpenAI", "Gemini", etc.
    public string Code { get; private set; }       // "opencode", "openai", "gemini", etc.
    public string? BaseUrl { get; private set; }   // Upstream API base URL
    public string? ApiKey { get; private set; }    // Upstream API key (encrypted at rest)
    public int Priority { get; private set; }      // Internal failover ordering (P0=0, P1=1, P2=2)
    public bool IsEnabled { get; private set; }
    public int? MaxTokensPerRequest { get; private set; }
    public decimal CostPerInputToken { get; private set; }
    public decimal CostPerOutputToken { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public ICollection<Model> Models { get; private set; } = [];

    // Factory method
    public static AiProvider Create(string name, string code, int priority,
        string? baseUrl = null, string? apiKey = null,
        decimal costPerInput = 0, decimal costPerOutput = 0);

    public void Enable();
    public void Disable();
    public void UpdateCredentials(string? baseUrl, string? apiKey);
}
```

**Seeded providers** (from `GatewayDbContext.OnModelCreating`):

| ID | Code | Name | Base URL | Priority | Enabled |
|----|------|------|----------|----------|---------|
| `a1...001` | `opencode` | OpenCode | `http://localhost:8080` | 0 | ✅ |
| `a1...002` | `openai` | OpenAI | `https://api.openai.com/v1` | 1 | ❌ |
| `a1...003` | `gemini` | Gemini | — | 1 | ❌ |
| `a1...004` | `anthropic` | Anthropic | — | 2 | ❌ |
| `a1...005` | `ollama` | Ollama | `http://localhost:11434` | 2 | ❌ |

API keys are stored in the database (not config) and fetched at runtime by each service via `IAiProviderRepository`.

---

#### `ApiKey` — `src/Arkana.Domain/Entities/ApiKey.cs`

```csharp
public sealed class ApiKey
{
    public Guid Id { get; private set; }
    public string KeyHash { get; private set; }      // SHA-256 hash of raw key
    public string KeyPrefix { get; private set; }     // First 12 chars for UI display
    public string Name { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public ICollection<Model> AllowedModels { get; private set; } = [];

    public static ApiKey Create(string name, string keyHash, string keyPrefix,
        DateTimeOffset? expiresAt = null);

    public bool IsExpired();
    public void Deactivate();
    public void Activate();
    public bool CanAccessModel(Guid modelId);
}
```

Keys are generated as `arkana-<64-hex-chars>` and stored as SHA-256 hashes. The raw key is shown once at creation time and never persisted.

---

#### `Model` — `src/Arkana.Domain/Entities/Model.cs`

```csharp
public sealed class Model
{
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public string Name { get; private set; }         // "DeepSeek V4 Flash"
    public string Code { get; private set; }          // "deepseek-v4-flash"
    public bool IsEnabled { get; private set; }
    public decimal CostPerInputToken { get; private set; }
    public decimal CostPerOutputToken { get; private set; }
    public int? MaxTokensPerRequest { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public AiProvider Provider { get; private set; }
    public ICollection<ApiKey> AllowedByKeys { get; private set; } = [];
}
```

Seeded with **16 models** across 5 providers — each with cost-per-token pricing. Models are mapped to providers via `ProviderId` FK.

---

### 4.2 Interfaces

| Interface | File | Methods | Implementation |
|-----------|------|---------|----------------|
| `IAiProviderRepository` | `src/.../Domain/Interfaces/IAiProviderRepository.cs` | `GetAllAsync`, `GetByIdAsync`, `UpdateAsync` | `AiProviderRepository` |
| `IApiKeyRepository` | `src/.../Domain/Interfaces/IApiKeyRepository.cs` | `GetAllAsync`, `GetByKeyHashAsync`, `AddAsync`, `UpdateAsync` | `ApiKeyRepository` |
| `IModelRepository` | `src/.../Domain/Interfaces/IModelRepository.cs` | `GetAllAsync`, `GetByProviderIdAsync` | `ModelRepository` |
| `IChatCompletionService` | `src/.../Domain/Interfaces/IChatCompletionService.cs` | `CompleteAsync(ChatRequest, CancellationToken) → ChatResult` | `OpenCodeChatService`, `OpenAIChatService`, `CLIProxyAPIChatService` |
| `IModelRouter` | `src/.../Domain/Interfaces/IModelRouter.cs` | `ResolveAsync(provider?)`, `GetAllProvidersAsync()` | `ModelRouter` |
| `ITokenTracker` | `src/.../Domain/Interfaces/ITokenTracker.cs` | `RecordUsageAsync`, `GetUsageAsync`, `GetRecentUsageAsync` | `EfCoreTokenTracker`, `InMemoryTokenTracker` |
| `IRequestLogger` | `src/.../Domain/Interfaces/IRequestLogger.cs` | `RecordAsync` | `EfCoreRequestLogger` |
| `IN8nService` | `src/.../Domain/Interfaces/IN8nService.cs` | `GetWorkflowsAsync`, `TriggerWorkflowAsync` | `N8nService` |

### 4.3 Value Objects

#### `ChatRequest` & `ChatResult` — `src/.../Domain/Interfaces/IChatCompletionService.cs`

```csharp
public sealed record ChatRequest
{
    public string Model { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public object? ToolChoice { get; init; }
    public string? UserId { get; init; }
}

public sealed record ChatResult
{
    public string Content { get; init; } = string.Empty;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public string Model { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsSuccess => ErrorMessage is null;
    public IReadOnlyList<ToolCallInfo>? ToolCalls { get; init; }
}
```

**Note:** Temperature, MaxTokens, and other model inference parameters were removed from `ChatRequest`. The gateway does NOT decompose model parameters — the upstream provider handles defaults. See commit `37d72a6`.

#### `TokenUsage` — `src/.../Domain/ValueObjects/TokenUsage.cs`

```csharp
public sealed record TokenUsage(string Provider, string Model,
    int InputTokens, int OutputTokens, decimal Cost,
    TimeSpan Duration, string? ApiKeyName);
```

#### `RequestLog` — `src/.../Domain/ValueObjects/RequestLog.cs`

Full request/response record with messages, tool calls, response content, errors, and cost. Stored as JSONB in PostgreSQL.

#### `ToolCallInfo` — `src/.../Domain/ValueObjects/ToolCallInfo.cs`

```csharp
public sealed record ToolCallInfo
{
    public string Id { get; init; }
    public string Type { get; init; }
    public string FunctionName { get; init; }
    public string FunctionArguments { get; init; }
}
```

### 4.4 Domain Services

#### `ApiKeyHasher` — `src/.../Domain/Services/ApiKeyHasher.cs`

```csharp
public static class ApiKeyHasher
{
    public static string Hash(string apiKey);          // SHA-256 → lowercase hex
    public static string GenerateApiKey();             // "arkana-<64 hex chars>"
    public static string ExtractPrefix(string rawApiKey); // First 12 chars
}
```

### 4.5 Domain Models

#### `WorkflowInfo` — `src/.../Domain/Models/WorkflowInfo.cs`

```csharp
public sealed record WorkflowInfo(string Id, string Name, bool Active);
```

---

## 5. Application Layer (CQRS)

**Project:** `src/Arkana.Application/`  
**Purpose:** MediatR commands, queries, handlers, and validators that orchestrate business operations.

### 5.1 Structure

```
Arkana.Application/
├── Common/Models/
│   └── Result.cs                    # Generic operation result wrapper
├── Features/
│   ├── Admin/Commands/
│   │   └── CreateApiKeyCommand.cs   # Create API key command
│   └── Chat/
│       ├── Commands/
│       │   ├── SendChatCommand.cs           # Chat request DTO
│       │   └── SendChatCommandValidator.cs  # FluentValidation rules
│       ├── Handlers/
│       │   ├── SendChatHandler.cs           # Routes + executes chat
│       │   └── GetTokenUsageHandler.cs      # Retrieves usage stats
│       └── Queries/
│           └── GetTokenUsageQuery.cs        # Usage query DTO
└── DependencyInjection.cs           # Service registration
```

### 5.2 SendChatHandler — Full Flow

**File:** `src/Arkana.Application/Features/Chat/Handlers/SendChatHandler.cs`

This is the core handler for non-streaming chat requests:

```
1. Receive SendChatCommand (model, messages, tools, toolChoice)
2. Look up model from DB (for pricing + permission)
3. Validate API key against allowed models
4. If invalid → return error result
5. Resolve provider via IModelRouter (preferred → default P0)
6. Convert DTO messages → domain ChatMessage objects
7. Create domain ChatRequest from command
8. Execute: provider.CompleteAsync(chatRequest)
9. Record token usage via ITokenTracker
10. Log full request/response via IRequestLogger
11. Return SendChatResult (content, tokens, cost, toolCalls)
```

Key design: **Temperature and MaxTokens are NOT in SendChatCommand.** They were removed because they're model inference parameters, not gateway concerns.

### 5.3 SendChatCommandValidator

**File:** `src/Arkana.Application/Features/Chat/Commands/SendChatCommandValidator.cs`

```csharp
public sealed class SendChatCommandValidator : AbstractValidator<SendChatCommand>
{
    public SendChatCommandValidator()
    {
        RuleFor(x => x.Messages).NotEmpty()
            .WithMessage("At least one message is required");
        RuleFor(x => x.Messages)
            .Must(m => m.Any(msg => msg.Role == "user"))
            .WithMessage("At least one user message is required");
    }
}
```

**Note:** Temperature and MaxTokens validation were removed in `37d72a6`.

### 5.4 DTOs

#### `SendChatCommand` — `src/.../Application/Features/Chat/Commands/SendChatCommand.cs`

```csharp
public sealed record SendChatCommand : IRequest<SendChatResult>
{
    public string Model { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessageDto> Messages { get; init; } = [];
    public IReadOnlyList<ToolDefinitionDto>? Tools { get; init; }
    public JsonElement? ToolChoice { get; init; }
    public string? PreferredProvider { get; init; }
    public string? ApiKey { get; init; }
}
```

#### `SendChatResult` — same file

```csharp
public sealed record SendChatResult
{
    public string Content { get; init; }
    public string Provider { get; init; }
    public string Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCost { get; init; }
    public long DurationMs { get; init; }
    public IReadOnlyList<ToolCallInfo>? ToolCalls { get; init; }
}
```

---

## 6. Infrastructure Layer

**Project:** `src/Arkana.Infrastructure/`  
**Purpose:** EF Core persistence, AI provider HTTP clients, token tracking, request logging, workflow integration.

### 6.1 Structure

```
Arkana.Infrastructure/
├── AI/
│   ├── OpenCodeChatService.cs      # OpenCode provider (P0)
│   ├── OpenAIChatService.cs        # OpenAI provider (P1 fallback)
│   ├── CLIProxyAPIChatService.cs   # CLIProxyAPI provider
│   └── ModelRouter.cs              # Provider selection/failover
├── Migrations/
│   ├── 20260606051657_InitialCreate.cs
│   ├── 20260606075621_AddModelsAndKeyModelPermissions.cs
│   ├── 20260606104738_AddPersistentTokenTrackingAndRequestLogs.cs
│   └── 20260607173444_AddApiKeyKeyPrefix.cs
├── Persistence/
│   ├── Entities/
│   │   ├── TokenUsageEntity.cs     # EF entity for usage records
│   │   └── RequestLogEntity.cs     # EF entity for request logs
│   ├── Repositories/
│   │   ├── AiProviderRepository.cs
│   │   ├── ApiKeyRepository.cs
│   │   └── ModelRepository.cs
│   └── GatewayDbContext.cs         # EF Core DbContext with seed data
├── Services/
│   ├── ApiKeyBootstrapService.cs   # Seeds env var key into DB on startup
│   ├── EfCoreTokenTracker.cs       # Persistent token tracking
│   ├── InMemoryTokenTracker.cs     # Fallback in-memory tracking
│   ├── EfCoreRequestLogger.cs      # Full request/response logging
│   ├── N8nOptions.cs               # Configuration POCO for n8n
│   └── N8nService.cs               # n8n REST client
└── DependencyInjection.cs          # Service registration
```

### 6.2 AI Provider Services

All three providers implement `IChatCompletionService`:

| Service | Provider Name | Code | Priority | HTTP Client Name | Base URL Source |
|---------|---------------|------|----------|-----------------|-----------------|
| `OpenCodeChatService` | `"OpenCode"` | `opencode` | P0 | `opencode` | DB → config → default |
| `OpenAIChatService` | `"OpenAI"` | `openai` | P1 | `openai` | Hardcoded + DB API key |
| `CLIProxyAPIChatService` | `"CLIProxyAPI"` | `cliproxyapi` | P3 | `cliproxyapi` | DB → env → default |

**API key resolution pattern** (all three services):

```csharp
// Load API key from the database (AiProviders table) at runtime
var providers = await _providerRepo.GetAllAsync(ct);
var provider = providers.FirstOrDefault(p =>
    p.Code.Equals("opencode", StringComparison.OrdinalIgnoreCase));
var apiKey = provider?.ApiKey
    ?? Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY")
    ?? string.Empty;
```

**Tool call support** (all three services):
- Parse `tool_calls` from the upstream response
- Convert to `ToolCallInfo` domain objects
- Return in `ChatResult.ToolCalls`

#### OpenCodeChatService — `OpenCodeChatService.cs`

- Builds request body as `Dictionary<string, object?>` for full serialization control
- Forwards `tools` array and `tool_choice` if present
- Handles tool role messages (`tool_call_id`) and assistant tool calls
- Deserializes OpenAI-compatible response with `tool_calls` support
- Returns `ChatResult` with parsed tool calls

#### OpenAIChatService — `OpenAIChatService.cs`

- Uses anonymous type for request body
- Hardcoded base URL: `https://api.openai.com/v1/chat/completions`
- Same tool call parsing as OpenCode
- Currently disabled by default (seeded as `IsEnabled = false`)

#### CLIProxyAPIChatService — `CLIProxyAPIChatService.cs`

- Wraps CLIProxyAPI as OpenAI-compatible provider
- Base URL from DB → env var `CLIPROXYAPI_BASE_URL` → `http://localhost:12345`
- API key from DB → env var `CLIPROXYAPI_API_KEY`
- Same tool call parsing pattern

### 6.3 ModelRouter — `ModelRouter.cs`

```csharp
internal sealed class ModelRouter : IModelRouter
{
    // Providers ordered by priority: OpenCode(0) → OpenAI(1) → Gemini(2) → CLIProxyAPI(3) → Anthropic(4) → Ollama(5)
    public Task<IChatCompletionService> ResolveAsync(string? preferredProvider = null);
    public Task<IReadOnlyList<IChatCompletionService>> GetAllProvidersAsync();
}
```

Returns P0 (OpenCode) by default. If `preferredProvider` is specified, returns the matching provider.

### 6.4 GatewayDbContext — `GatewayDbContext.cs`

The EF Core context manages 5 DbSets:

```csharp
public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
public DbSet<AiProvider> AiProviders => Set<AiProvider>();
public DbSet<Model> Models => Set<Model>();
public DbSet<TokenUsageEntity> TokenUsages => Set<TokenUsageEntity>();
public DbSet<RequestLogEntity> RequestLogs => Set<RequestLogEntity>();
```

**Key EF Core configurations:**
- `ApiKey.KeyHash` — unique index, max 128 chars
- `AiProvider.Code` — unique index
- `Model.ProviderId + Code` — composite unique index
- `ApiKey ↔ Model` — many-to-many join table `ApiKeyModels`
- `TokenUsageEntity` — indexed on Timestamp, Provider, Model
- `RequestLogEntity` — JSONB columns for `MessagesJson`, `ToolCallsJson`
- Decimal columns with `numeric(20,10)` precision

**Seed data:** 5 providers + 16 models with exact cost-per-token pricing.

### 6.5 Token Tracking

Two implementations of `ITokenTracker`:

| Implementation | Scope | Database | Purpose |
|---------------|-------|----------|---------|
| `EfCoreTokenTracker` | Production | PostgreSQL | Persistent storage, survives restarts |
| `InMemoryTokenTracker` | Development | ConcurrentBag | Quick dev/testing, data lost on restart |

**`EfCoreTokenTracker`** writes `TokenUsageEntity` records to the `TokenUsages` table with: provider, model, input/output tokens, cost, duration, ApiKeyName, timestamp.

### 6.6 Request Logger

**`EfCoreRequestLogger`** implements `IRequestLogger` and stores full request/response data in the `RequestLogs` table as JSONB:

```csharp
public sealed record RequestLog
{
    public string Provider { get; init; }
    public string Model { get; init; }
    public string? ApiKeyName { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; }    // Serialized as JSONB
    public string ResponseContent { get; init; }
    public IReadOnlyList<ToolCallInfo>? ToolCalls { get; init; } // Serialized as JSONB
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal Cost { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### 6.7 ApiKeyBootstrapService — `ApiKeyBootstrapService.cs`

`IHostedService` that runs on startup:

```
1. Query AiProviders table for OpenCode provider
2. If provider exists AND ApiKey is null/empty:
   a. Read OPENCODE_GO_API_KEY from environment
   b. If env var is set → update provider.ApiKey in database
3. If DB not ready yet → log warning, skip (key can be set via admin API)
```

This enables the "env var → DB" bootstrap pattern while allowing future key rotations via the admin API.

### 6.8 n8n Integration

**`N8nService`** — Singleton HTTP client for n8n REST API:

- Login via `/rest/login` with email/password (cookie-based auth)
- `GetWorkflowsAsync()` — GET `/rest/workflows`
- `TriggerWorkflowAsync(string id)` — POST `/rest/workflows/{id}/run`

Configured via `N8nOptions` bound from `appsettings.json` → `N8n` section.

---

## 7. Gateway API Layer

**Project:** `src/Arkana.Gateway.Api/`  
**Purpose:** HTTP endpoints, middleware, Blazor dashboard, and the application entrypoint.

### 7.1 Program.cs — Application Bootstrap

**File:** `src/Arkana.Gateway.Api/Program.cs`

```csharp
// ── Service Registration ──
builder.Services.AddServiceDefaults();           // Health checks, OpenTelemetry
builder.Services.AddApplicationServices();       // MediatR, validators
builder.Services.AddInfrastructureServices(      // EF Core, repos, AI clients
    postgresConnectionString: "Host=...",
    openCodeBaseUrl: "...");

// Blazor Server
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<UserTimeService>();
builder.Services.AddSingleton<ActiveStreamCounter>();

// ── Middleware Pipeline (order matters) ──
app.UseStaticFiles();          // Blazor static assets
app.UseAntiforgery();          // Blazor anti-forgery
app.UseCors();                 // AllowAnyOrigin
app.UseWhen(path => {          // Conditional API key auth
    // Skip auth for: /dashboard, /cost, /api-keys, /providers, /workflows,
    // /logs, /v1/models, /_framework, /_content, /_blazor, /, /app.css
}, subApp => subApp.UseMiddleware<ApiKeyAuthMiddleware>());
app.UseMiddleware<TokenTrackingMiddleware>();

// ── Endpoints ──
app.MapDefaultEndpoints();     // /health, /alive
app.MapChatEndpoints();        // POST /v1/chat/completions
app.MapResponsesEndpoints();   // POST /v1/responses
app.MapAdminEndpoints();       // /admin/*

// Blazor Dashboard
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
```

### 7.2 Endpoints

#### ChatEndpoints — `ChatEndpoints.cs`

**Route:** `POST /v1/chat/completions`  
**Purpose:** OpenAI-compatible chat completions, both streaming and non-streaming.

Two code paths within the same endpoint:

**Path A — Streaming (`request.Stream == true`):**

```
1. Read upstream OpenCode API key from DB (IAiProviderRepository)
2. Set SSE headers (Content-Type, Cache-Control, Connection, X-Accel-Buffering)
3. Increment ActiveStreamCounter
4. Build body dict: {model, messages, stream:true} + optional tools/toolChoice
5. Forward to upstream via HttpClient ("opencode-streaming", 10-min timeout)
6. Stream SSE events: upstream → client (relay each "data:" line)
7. Track usage: parse prompt_tokens/completion_tokens from trailing usage
8. Log usage to ITokenTracker
9. Finally: Decrement ActiveStreamCounter
```

**Path B — Non-streaming (`request.Stream != true`):**

```
1. Build SendChatCommand from ChatCompletionRequest DTO
2. Send via MediatR → SendChatHandler
3. Receive SendChatResult
4. Return JSON response: {choices: [{message: {role, content, tool_calls}}]}
```

**DTO:** `ChatCompletionRequest` (bottom of ChatEndpoints.cs):

```csharp
public sealed record ChatCompletionRequest
{
    public string? Model { get; init; }
    public IReadOnlyList<ApplicationDtoMessage>? Messages { get; init; }
    public IReadOnlyList<ApplicationDtoToolDefinition>? Tools { get; init; }
    public JsonElement? ToolChoice { get; init; }
    public bool? Stream { get; init; }
}
```

**Note:** `MaxTokens` and `Temperature` were removed from this DTO in `37d72a6`.

---

#### ResponsesEndpoints — `ResponsesEndpoints.cs`

**Route:** `POST /v1/responses`  
**Purpose:** OpenAI Responses API → Chat Completions translation layer. Required by Codex CLI/Desktop which speaks only the Responses API wire format.

**Format translation:**

| Responses API (incoming) | Chat Completions (outgoing) |
|--------------------------|----------------------------|
| `input` (array of message objects) | `messages` |
| `model` | `model` |
| `max_output_tokens` | `max_tokens` (only if value set) |
| `tools` (Responses format) | `tools` (Chat Completions format, via `TranslateTools()`) |
| `tool_choice` (Responses format) | `tool_choice` (Chat Completions format) |
| `instructions` (system message) | prepended as `{"role":"system","content":...}` |
| `stream` | `stream` |

**`TranslateTools()` function** (lines 976-1010):

```csharp
// Input: Responses API tool format
// {"type":"function","name":"Bash","description":"...","input_schema":{...}}

// Output: Chat Completions tool format
// {"type":"function","function":{"name":"Bash","description":"...","parameters":{...}}}

private static List<object?> TranslateTools(JsonElement toolsEl);
```

**SSE event translation:**
- `response.created` → emitted immediately
- `response.in_progress` → emitted before upstream call
- Upstream Chat Completions SSE → translated to Responses API SSE events
  - `response.output_item.added` (text content)
  - `response.output_item.done`
  - `response.text.delta`
  - `response.text.done`
  - `response.response.completed`

---

#### AdminEndpoints — `AdminEndpoints.cs`

**Route group:** `/admin`

| Method | Path | Description | Auth |
|--------|------|-------------|------|
| `POST` | `/admin/api-keys` | Create API key | Bypassed (Phase 1) |
| `GET` | `/admin/api-keys` | List all API keys | Bypassed |
| `PUT` | `/admin/api-keys/{id}/toggle` | Enable/disable key | Bypassed |
| `DELETE` | `/admin/api-keys/{id}` | Delete API key | Bypassed |
| `GET` | `/admin/providers` | List providers (no key shown) | Bypassed |
| `PUT` | `/admin/providers/{id}/toggle` | Enable/disable provider | Bypassed |
| `PUT` | `/admin/providers/{id}/apikey` | Set provider API key | Bypassed |
| `GET` | `/admin/logs` | Recent usage logs | Bypassed |
| `GET` | `/admin/stats` | Aggregate stats | Bypassed |

> **Note:** Admin endpoints bypass API key auth in Phase 1. Auth will be added in a later phase.

**`CreateApiKey`** generates `arkana-<64-hex>`, hashes with SHA-256, stores hash + prefix. Returns the plaintext key once.

**`SetProviderApiKeyRequest`:**

```csharp
public sealed record SetProviderApiKeyRequest(string ApiKey);
```

### 7.3 Services (Gateway-Scoped)

#### `ActiveStreamCounter` — `Services/ActiveStreamCounter.cs`

Thread-safe singleton counting active SSE streaming connections.

```csharp
public sealed class ActiveStreamCounter
{
    public int ActiveCount => _count;        // Interlocked reads
    public event Action? OnCountChanged;      // Blazor dashboard real-time updates
    public void Increment();                  // Interlocked.Increment
    public void Decrement();                  // Interlocked.Decrement
}
```

Used by both `ChatEndpoints` (streaming) and `ResponsesEndpoints` (streaming). Dashboard subscribes to `OnCountChanged` for real-time updates.

#### `DashboardService` — `Services/DashboardService.cs`

In-process service wrapping all repositories for Blazor dashboard pages. Provides:

- `GetDashboardStatsAsync()` — counts, stream count, workflow counts
- `GetUsageStatsAsync(TimeRange, provider?, key?)` — filtered usage statistics
- `GetModelUsageAsync(TimeRange, provider?, key?)` — per-model usage breakdown
- `GetProvidersWithModelsAsync()` — provider + model tree
- `GetApiKeysAsync()` — API key listing
- `GetCostChartDataAsync(year, month, filter?)` — daily cost chart data
- `CreateApiKeyAsync(name, modelIds)` — create key with model permissions
- `ToggleProviderAsync(id)`, `ToggleApiKeyAsync(id)` — enable/disable

#### `UserTimeService` — `Services/UserTimeService.cs`

Timezone-aware time display. Reads `DefaultTimeZone` from `appsettings.json` (default: `Asia/Jakarta`).

---

## 8. Service Defaults & AppHost

### 8.1 Arkana.ServiceDefaults

**Project:** `src/Arkana.ServiceDefaults/`

Shared configuration for all gateway services:

- **Health checks** — `/health`, `/alive` endpoints
- **OpenTelemetry** — Tracing, metrics, logging
- **Default service settings** — CORS, JSON serialization

**Files:**
- `Extensions.cs` — `AddServiceDefaults()`, `MapDefaultEndpoints()`
- `EndpointExtensions.cs` — Additional endpoint extension methods

### 8.2 Arkana.AppHost

**Project:** `src/Arkana.AppHost/`  

.NET Aspire orchestration project. Launches the Gateway API with environment config.

**`AppHost.cs`:**

```csharp
var builder = DistributedApplication.CreateBuilder(args);
var api = builder.AddProject<Projects.Arkana_Gateway_Api>("ai-gateway-api")
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:5000");
builder.Build().Run();
```

**Configuration files:**
- `appsettings.json` — PostgreSQL connection string, Aspire settings
- `appsettings.Development.json` — Dev overrides
- `launchSettings.json` — Dev profiles
- `aspire.config.json` — Aspire manifest config

---

## 9. Blazor Dashboard

**Location:** `src/Arkana.Gateway.Api/Components/`

### 9.1 Pages

| Route | Page | Description |
|-------|------|-------------|
| `/` | **Dashboard** | KPI cards (API keys, providers, active streams, requests today, tokens today, cost today), usage chart (daily/weekly/monthly/yearly/all), provider/model usage table, recent request logs |
| `/logs` | **Request Logs** | Full request/response logs with expandable details (messages, tool calls, timing) — 100 entries with auto-refresh and delete |
| `/api-keys` | **API Keys** | List keys with prefix, status, created date, expiry; create new key (with model permissions), toggle active/inactive, delete |
| `/providers` | **Providers** | List all providers with status, toggle enable/disable, manage per-provider models |
| `/cost` | **Cost Analytics** | Monthly cost breakdown chart by model with stacked bar visualization, date navigation, model/key filters |

### 9.2 Layout

- `Layout/MainLayout.razor` — Sidebar + top bar + main content
- `Layout/NavMenu.razor` — Navigation links
- `Layout/UserTimeDisplay.razor` — Timezone-aware clock

### 9.3 Shared Components

- `Shared/CostChart.razor` — Pure CSS stacked bar chart (zero JS)
- `Shared/UsageChart.razor` — Time-range usage chart
- `Shared/StatusBadge.razor` — Active/Inactive badges

### 9.4 Rendering Mode

**Interactive Server** — Blazor Server with real-time SignalR connections. Dashboard stats auto-refresh, `ActiveStreamCounter` fires `OnCountChanged` events that update the UI in real-time without polling.

### 9.5 Styling

- Custom CSS (no JavaScript interop)
- Light blue gradient header palette (`#81D4FA`, `#4FC3F7`, `#29B6F6`)
- Left-aligned headers (flex-start)
- Mobile-responsive sidebar

---

## 10. Docker Infrastructure

### 10.1 Docker Compose — `deploy/docker-compose.yml`

```yaml
services:
  postgres:     # PostgreSQL 16 Alpine (port 5432)
  redis:        # Redis 7 Alpine (port 6379)
  n8n:          # n8n workflow engine (port 5678)
  gateway:      # AI Gateway (port 5011) — built from Dockerfile
```

**Gateway service** environment variables:

| Variable | Value | Purpose |
|----------|-------|---------|
| `ASPNETCORE_URLS` | `http://0.0.0.0:5011` | Listen port |
| `ConnectionStrings__Postgres` | `Host=postgres;...` | DB connection |
| `N8n__BaseUrl` | `http://n8n:5678` | n8n internal URL |
| `N8n__UserEmail` | `${N8N_USER_EMAIL:-}` | n8n login |
| `N8n__UserPassword` | `${N8N_USER_PASSWORD:-}` | n8n password |

**Note:** `OPENCODE_GO_API_KEY` was removed from the docker-compose environment. The gateway now reads the upstream API key from the database.

**Volumes:** `pgdata`, `redisdata`, `n8ndata`, `dataprotection`

**Network:** `arkana-net` (bridge)

### 10.2 Dockerfile

Multi-stage build:

```
Stage 1 (build):
  FROM mcr.microsoft.com/dotnet/sdk:10.0
  - Copy .csproj files for layer caching
  - dotnet restore Arkana.slnx
  - dotnet publish (WITHOUT --no-restore — needed for Blazor static assets)

Stage 2 (runtime):
  FROM mcr.microsoft.com/dotnet/aspnet:10.0
  - Install curl for health checks
  - Copy published output
  - HEALTHCHECK: curl http://localhost:5011/
  - ENTRYPOINT: dotnet Arkana.Gateway.Api.dll
```

---

## 11. Middleware Pipeline

### 11.1 Pipeline Order

```
Request →
  [1] Static Files (Blazor assets: _framework/*, _content/*, app.css)
  [2] CORS (AllowAnyOrigin, AllowAnyMethod, AllowAnyHeader)
  [3] Conditional API Key Auth (UseWhen — exempts dashboard paths)
  [4] Token Tracking (timing, usage metadata)
  [5] Route Endpoint (/v1/chat/completions, /v1/responses, /admin/*, /dashboard)
```

### 11.2 ApiKeyAuthMiddleware — `Middleware/ApiKeyAuthMiddleware.cs`

```csharp
public async Task InvokeAsync(HttpContext context, IApiKeyRepository repo)
{
    // Skip auth for /health, /admin (Phase 1)
    if (path.StartsWithSegments("/health") || path.StartsWithSegments("/admin"))
        { await _next(context); return; }

    // Extract key: X-Api-1 header or Authorization: Bearer
    var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault()
              ?? context.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

    if (string.IsNullOrEmpty(apiKey))
        { return 401 "API key required"; }

    // Hash and validate
    var hash = ApiKeyHasher.Hash(apiKey);
    var key = await repo.GetByKeyHashAsync(hash);
    if (key is null || !key.IsActive || key.IsExpired())
        { return 401 "Invalid or expired API key"; }

    // Store for downstream
    context.Items["ApiKeyId"] = key.Id;
    context.Items["ApiKeyName"] = key.Name;
    context.Items["ApiKeyModelIds"] = key.AllowedModels.Select(m => m.Id).ToArray();
}
```

**Conditional auth** (from Program.cs `UseWhen`):
- Auth applied to: `/v1/chat/completions`, `/v1/responses`, `/admin/*`, etc.
- Auth bypassed for: `/dashboard`, `/cost`, `/api-keys`, `/providers`, `/workflows`, `/logs`, `/v1/models`, `/_framework`, `/_content`, `/_blazor`, `/`, `/app.css`

### 11.3 TokenTrackingMiddleware — `Middleware/TokenTrackingMiddleware.cs`

```csharp
public async Task InvokeAsync(HttpContext context, ITokenTracker tracker)
{
    var sw = Stopwatch.StartNew();
    context.Items["__sw"] = sw;
    context.Items["__tracker"] = tracker;
    context.Items["__provider"] = "unknown";
    context.Items["__model"] = "unknown";
    context.Items["__inputTokens"] = 0;
    context.Items["__outputTokens"] = 0;
    context.Items["__cost"] = 0m;

    await _next(context);

    sw.Stop();
    // Sw should be used by endpoints to record usage after execution
}
```

The middleware sets up tracking items in `HttpContext.Items` for endpoints to populate. The actual token recording happens in the endpoint handlers (ChatEndpoints lines 178-195).

---

## 12. Authentication & Authorization

### 12.1 Two-Layer Auth Model

```
Layer 1: Gateway Auth
  Client → Gateway
  Header: Authorization: Bearer arkana-xxx
  Validated against: PostgreSQL ApiKeys table (SHA-256 hashed)
  Purpose: Client authentication, tenant isolation

Layer 2: Upstream Auth
  Gateway → Upstream Provider
  Header: Authorization: Bearer sk-xxx (OpenCode API key)
  Validated against: AiProvider.ApiKey in database (or env var fallback)
  Purpose: Provider authentication
```

### 12.2 Gateway API Key Format

```
arkana-<64 lowercase hex characters>
Example: YOUR_GATEWAY_API_KEY
```

### 12.3 API Key Permissions

API keys can be restricted to specific models. The `ApiKey.AllowedModels` collection defines which models a key can access:

- If `AllowedModels` is empty → key has access to ALL models
- If `AllowedModels` has entries → key can ONLY use those models
- Check performed in `SendChatHandler.Handle()` via `key.CanAccessModel(modelId)`

### 12.4 Auth Bypass (Current Phase)

In Phase 1, the following paths bypass API key auth:
- Admin endpoints (`/admin/*`) — will be secured in a later phase
- Dashboard UI (`/dashboard`, `/api-keys`, `/providers`, etc.)
- Model listing (`/v1/models`)
- Blazor assets (`/_framework`, `/_content`, `/_blazor`)
- Health check (`/health`)
- Root (`/`)

---

## 13. Request Flow: End to End

### 13.1 Streaming Chat Request (OpenCode CLI → Gateway → Upstream)

```
┌──────────┐     ┌──────────┐     ┌──────────────┐     ┌──────────┐
│ OpenCode  │     │  Gateway  │     │  PostgreSQL   │     │ opencode │
│   CLI     │     │  :5011    │     │               │     │   .ai    │
└─────┬─────┘     └─────┬─────┘     └──────┬────────┘     └────┬─────┘
      │                  │                  │                    │
      │ POST /v1/chat/   │                  │                    │
      │ completions      │                  │                    │
      │ {stream:true,    │                  │                    │
      │  tools, msgs}    │                  │                    │
      │ Auth: arkana-xxx   │                  │                    │
      │─────────────────▶│                  │                    │
      │                  │                  │                    │
      │              ┌───┴───┐              │                    │
      │              │Auth?  │              │                    │
      │              │ path  │              │                    │
      │              │ /v1/* │─────────▶ Hash key, query DB      │
      │              │       │◀───────── Key info                │
      │              │ 401?  │              │                    │
      │              └───┬───┘              │                    │
      │                  │ ✅ Valid          │                    │
      │                  │                  │                    │
      │              ┌───┴───┐              │                    │
      │              │Stream?│              │                    │
      │              │  true │              │                    │
      │              └───┬───┘              │                    │
      │                  │                  │                    │
      │              ┌───┴───┐              │                    │
      │              │Get    │              │                    │
      │              │upstream              │                    │
      │              │API key│─────────▶ query AiProviders       │
      │              │from DB│◀───────── apiKey                  │
      │              └───┬───┘              │                    │
      │                  │                  │                    │
      │              ┌───┴───┐              │                    │
      │              │Build body            │                    │
      │              │{model,               │                    │
      │              │ messages,            │                    │
      │              │ stream:true,         │                    │
      │              │ tools,               │                    │
      │              │ tool_choice}         │                    │
      │              └───┬───┘              │                    │
      │                  │                  │                    │
      │                  │ HTTP POST chat/completions            │
      │                  │ Auth: Bearer sk-xxx                   │
      │                  │──────────────────────────────────────▶│
      │                  │                  │                    │
      │                  │                  │           ┌───▶ LLM
      │                  │                  │           │ (DeepSeek
      │                  │                  │           │  V4 Flash)
      │                  │◀──────────────────────────────────────┤
      │                  │      SSE events   │                    │
      │                  │      data: {...}  │                    │
      │                  │      data: [...}  │                    │
      │                  │                  │                    │
      │  SSE events      │                  │                    │
      │  data: {...}     │                  │                    │
      │◀─────────────────│                  │                    │
      │  data: [DONE]    │                  │                    │
      │◀─────────────────│                  │                    │
      │                  │                  │                    │
      │              ┌───┴───┐              │                    │
      │              │Log    │─────────▶ Record TokenUsage       │
      │              │usage  │              │                    │
      │              └───────┘              │                    │
```

### 13.2 Non-Streaming Chat Request (via MediatR)

```
CLI → POST /v1/chat/completions
      Auth: Bearer arkana-xxx
      Body: {model, messages, tools}

      ↓

ChatEndpoints (non-streaming path)
  ↓
Validate API key (same auth middleware)
  ↓
Build SendChatCommand from request DTO
  ↓
 mediator.Send(command)
  ↓
SendChatHandler.Handle(command)
  ↓
1. Look up model from DB (pricing + permission)
2. Validate API key & check model access
3. Resolve provider via ModelRouter (default: OpenCode)
4. Convert DTO → domain types
5. Build ChatRequest
6. Call provider.CompleteAsync(chatRequest)
7. Record usage via ITokenTracker
8. Log full request via IRequestLogger
9. Return SendChatResult
  ↓

ChatEndpoints
  ↓
Return JSON: {choices: [{message: {role, content, tool_calls}}]}
```

### 13.3 Responses API Flow (Codex CLI)

```
Codex CLI → POST /v1/responses (Responses API format)
            Auth: Bearer arkana-xxx
            Body: {model, input: [...], tools: [...], stream}

            ↓

ResponsesEndpoints
  ↓
1. Read raw body into string
2. Deserialize to ResponsesRequest DTO
3. Determine model + provider (from config or DB)
4. Translate Responses → Chat Completions format:
   - input → messages
   - tools → TranslateTools()
   - max_output_tokens → max_tokens
   - instructions → prepend as system message
5. Get upstream API key from DB
6. Send to upstream API
7. Translate SSE events back to Responses API format
8. Usage tracking + logging
```

---

## 14. AI Provider Integration

### 14.1 Provider Priority Chain

```
P0: OpenCode (opencode.ai)           — always tried first
P1: OpenAI (api.openai.com)          — fallback, currently disabled
P2: Gemini (generativelanguage.googleapis.com) — fallback, disabled
P3: CLIProxyAPI (localhost:12345)    — local proxy for subscription CLIs
P4: Anthropic (api.anthropic.com)    — fallback, disabled
P5: Ollama (localhost:11434)         — local models, disabled
```

### 14.2 Adding a New Provider

1. Add row to `AiProviders` table (or seed data in `GatewayDbContext`)
2. Add associated models to `Models` table
3. Create service implementing `IChatCompletionService`:
   ```csharp
   internal sealed class NewProviderChatService : IChatCompletionService
   {
       public string ProviderName => "NewProvider";
       // Constructor receives HttpClient + IAiProviderRepository
       // CompleteAsync() fetches API key from DB, builds request, sends, parses response
   }
   ```
4. Register in `DependencyInjection.cs`:
   ```csharp
   services.AddHttpClient<IChatCompletionService, NewProviderChatService>("newprovider");
   ```
5. Add to `ModelRouter` priority list

### 14.3 Provider API Key Management

Keys are stored in the `AiProviders` table and managed via:

```bash
# Set provider API key
curl -X PUT http://localhost:5011/admin/providers/{id}/apikey \
  -H "Content-Type: application/json" \
  -d '{"apiKey": "sk-xxx"}'

# View providers (key is NOT returned — only hasKey boolean)
curl http://localhost:5011/admin/providers
```

Runtime key resolution in each service:

```csharp
var providers = await _providerRepo.GetAllAsync(ct);
var provider = providers.FirstOrDefault(p =>
    p.Code.Equals("opencode", StringComparison.OrdinalIgnoreCase));
var apiKey = provider?.ApiKey
    ?? Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY")
    ?? string.Empty;
```

---

## 15. CLI Integrations

### 15.1 OpenCode CLI

**Config** (`~/.config/opencode/opencode.jsonc`):

```jsonc
{
  "providers": {
    "arkana-gateway": {
      "baseURL": "http://localhost:5011/v1",
      "apiKey": "arkana-xxx",
      "models": {
        "deepseek-v4-flash": {}
      }
    }
  }
}
```

**Requests route through:** `POST /v1/chat/completions`

| Mode | Gateway Path | Tool Calls Work? |
|------|-------------|-----------------|
| Simple chat (`opencode run "hi"`) | Streaming → upstream | ✅ No tools needed |
| Tool calls (non-agent) | Streaming → upstream | ✅ For compatible tools |
| Agent mode (`opencode`) | Streaming → upstream | ❌ 403/1010 at opencode.ai |

**Tools sent in agent mode:** Bash, Read, Write, Task, TodoWrite, WebFetch (~110 KB system prompt with embedded tool definitions).

### 15.2 OpenClaude CLI (Valarions Claude)

**Config:** Uses standard OpenAI-compatible provider config pointing to gateway.

**Requests route through:** `POST /v1/chat/completions`

**Tools sent:** ~25+ tool definitions including Bash, Read, Write, Edit, Glob, Grep, FileSearch, LS, WebFetch, WebSearch, Task, TodoWrite, ProjectRead, Think, Plan, etc.

**Status through opencode.ai upstream:** ❌ 403/1010 block (same as OpenCode CLI)
**Status through direct provider upstream:** ✅ Works (can be configured via model router)

### 15.3 Codex CLI (OpenAI Codex)

**Requests route through:** `POST /v1/responses` (Responses API)

**Gateway translation:** Responses API → Chat Completions format

**Tools sent:** Bash, Read, Edit, Search (Responses API format with `input_schema`)

**Status through opencode.ai upstream:** ❌ 403/1010 block (same as others)
**Status through direct provider upstream:** ✅ Works

**Additional constraint:** Codex Desktop's sandbox restricts file access to permitted roots (default: `C:\Users\<user>\Documents\Codex`). This is a Codex-side limitation, not gateway-related.

### 15.4 Summary

| CLI | API Format | Gateway Path | Tool Calls (opencode.ai) | Tool Calls (direct provider) |
|-----|-----------|-------------|--------------------------|------------------------------|
| OpenCode | Chat Completions | `/v1/chat/completions` | ❌ 403/1010 | N/A |
| OpenClaude | Chat Completions | `/v1/chat/completions` | ❌ 403/1010 | ✅ |
| Codex | Responses | `/v1/responses` → Chat Completions | ❌ 403/1010 | ✅ |

See [docs/tool-calls-and-ai-gateway.md](docs/tool-calls-and-ai-gateway.md) for full analysis.

---

## 16. Tool Calling Architecture

### 16.1 How Tool Calls Flow

```
CLI                         Gateway                         Upstream
 │                           │                                │
 │ 1. Send request            │                                │
 │    with tools array        │                                │
 │───────────────────────────▶│                                │
 │                           │                                │
 │                           │ 2. Forward tools AS-IS          │
 │                           │    (no modification)           │
 │                           │───────────────────────────────▶│
 │                           │                                │
 │                           │           3. Process tools     │
 │                           │           Return tool_calls    │
 │                           │◀───────────────────────────────│
 │                           │                                │
 │ 4. Receive tool_calls     │                                │
 │◀──────────────────────────│                                │
 │                           │                                │
 │ 5. Execute tool locally   │                                │
 │    (bash, read file, etc.)│                                │
 │                           │                                │
 │ 6. Send tool_result       │                                │
 │───────────────────────────▶│───────────────────────────────▶│
 │                           │                                │
 │ 7. Receive text response  │                                │
 │◀──────────────────────────│◀───────────────────────────────│
```

### 16.2 The 403/1010 Security Block

When the upstream is `opencode.ai/zen/go/v1`:

```json
HTTP 403
{
  "error": {
    "code": 1010,
    "message": "Security policy violation: agent tool definitions are not allowed through this API endpoint"
  }
}
```

**Root cause:** opencode.ai's API has a server-side security gate that blocks agent-mode tool definitions. It detects proxied requests via:
- `User-Agent` header mismatch
- TLS handshake origin (Docker vs. native CLI)
- IP/network difference

**The gateway cannot fix this.** It faithfully forwards the request — the block is at opencode.ai before the request reaches the LLM.

### 16.3 Gateway's Role in Tool Calls

The gateway:
- ✅ Forwards `tools` array faithfully (Chat Completions endpoints)
- ✅ Translates `tools` format (Responses → Chat Completions via `TranslateTools()`)
- ✅ Forwards `tool_choice` faithfully
- ✅ Parses `tool_calls` from upstream response (all three services)
- ✅ Returns `tool_calls` to client
- ✅ Logs tool call usage (if they succeed)
- ❌ Cannot bypass opencode.ai's security policy
- ✅ Can route to a different provider that accepts tool definitions

---

## 17. Database Schema & Migrations

### 17.1 Entity Relationship Diagram

```
┌──────────────┐       ┌──────────────────┐       ┌──────────────┐
│   ApiKeys    │       │  ApiKeyModels    │       │    Models    │
├──────────────┤       │  (join table)    │       ├──────────────┤
│ PK Id (Guid) │◄─────▶│ PK ApiKeyId      │◄─────▶│ PK Id (Guid) │
│ KeyHash      │       │ PK ModelId       │       │ ProviderId   │◀──┐
│ KeyPrefix    │       └──────────────────┘       │ Name         │   │
│ Name         │                                   │ Code         │   │
│ IsActive     │                                   │ IsEnabled    │   │
│ CreatedAt    │       ┌──────────────────┐       │ CostPerInput │   │
│ ExpiresAt    │       │   AiProviders    │       │ CostPerOutput│   │
└──────────────┘       ├──────────────────┤       │ MaxTokens    │   │
                       │ PK Id (Guid)     │       │ FK ProviderId│───┘
┌──────────────────┐   │ Name             │       └──────────────┘
│  TokenUsages     │   │ Code (unique)    │
├──────────────────┤   │ BaseUrl          │       ┌──────────────────┐
│ PK Id (Guid)     │   │ ApiKey           │       │  RequestLogs     │
│ Provider         │   │ Priority         │       ├──────────────────┤
│ Model            │   │ IsEnabled        │       │ PK Id (Guid)     │
│ InputTokens      │   │ CostPerInput     │       │ Provider         │
│ OutputTokens     │   │ CostPerOutput    │       │ Model            │
│ Cost             │   │ CreatedAt        │       │ ApiKeyName       │
│ DurationTicks    │   └──────────────────┘       │ MessagesJson     │ ← JSONB
│ Timestamp        │                              │ ToolCallsJson    │ ← JSONB
│ ApiKeyName       │                              │ ResponseContent  │
└──────────────────┘                              │ InputTokens      │
                                                  │ OutputTokens     │
                                                  │ Cost             │
                                                  │ Duration         │
                                                  │ Timestamp        │
                                                  │ IsError          │
                                                  │ ErrorMessage     │
                                                  └──────────────────┘
```

### 17.2 Migrations

| Migration | Changes |
|-----------|---------|
| `20260606051657_InitialCreate` | ApiKeys, AiProviders, Models tables + seed data + many-to-many join table |
| `20260606075621_AddModelsAndKeyModelPermissions` | Extended model permissions, additional seed models |
| `20260606104738_AddPersistentTokenTrackingAndRequestLogs` | TokenUsages + RequestLogs tables |
| `20260607173444_AddApiKeyKeyPrefix` | Added KeyPrefix column to ApiKeys |

### 17.3 Seed Data

**5 providers** with exact cost-per-token pricing:
- OpenCode (P0) — `a1000000-0000-0000-0000-000000000001`
- OpenAI (P1, disabled) — `...002`
- Gemini (P1, disabled) — `...003`
- Anthropic (P2, disabled) — `...004`
- Ollama (P2, disabled) — `...005`

**16 models** with model codes and pricing:
- 8 OpenCode models (deepseek-v4-flash, deepseek-v4-pro, glm-5.1, glm-5, kimi-k2.5, kimi-k2.6, mimo-v2.5, mimo-v2.5-pro)
- 5 OpenAI models (gpt-4o, gpt-4o-mini, gpt-4.1, o3, o4-mini)
- 3 Gemini models (gemini-2.5-pro, gemini-2.5-flash, gemini-2.0-flash)
- 3 Anthropic models (claude-sonnet-4, claude-haiku-3.5, claude-opus-4)
- 4 Ollama models (llama-3.1-8b, llama-3.1-70b, mistral, codellama)

### 17.4 Creating Migrations

```bash
dotnet ef migrations add MigrationName \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api
```

### 17.5 Applying Migrations

```bash
# Development (local)
dotnet ef database update \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api

# Production (automatic — migrations run on startup via EnsureCreated or manual SQL)
```

---

## 18. Configuration Reference

### 18.1 appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=arkana;Username=arkana;Password=arkana_dev"
  },
  "ProviderOptions": {
    "OpenCode": {
      "BaseUrl": "https://opencode.ai/zen/go/v1",
      "ApiKey": "",
      "DefaultModel": "deepseek-v4-flash"
    },
    "OpenAI": {
      "ApiKey": ""
    }
  },
  "N8n": {
    "BaseUrl": "http://localhost:5678",
    "UserEmail": "",
    "UserPassword": ""
  },
  "DefaultTimeZone": "Asia/Jakarta"
}
```

**Note:** `ProviderOptions:OpenCode:ApiKey` is intentionally `""` (empty string). The gateway reads the API key from the database via `IAiProviderRepository`. The empty config value acts as a trigger to fall through to DB/env var.

### 18.2 Environment Variables

| Variable | Where Used | Purpose |
|----------|-----------|---------|
| `ConnectionStrings__Postgres` | Docker, env | PostgreSQL connection string override |
| `N8N_USER_EMAIL` | Docker compose | n8n admin email |
| `N8N_USER_PASSWORD` | Docker compose | n8n admin password |
| `N8N_ENCRYPTION_KEY` | Docker compose | n8n data encryption |
| `N8N_EMAIL` | Docker compose | n8n user email |
| `N8N_PASSWORD` | Docker compose | n8n user password |
| `ASPNETCORE_URLS` | Docker, AppHost | Gateway listen URL |

**Removed environment variables** (key now in DB):

| Removed Var | Replacement |
|-------------|-------------|
| `OPENCODE_GO_API_KEY` | `PUT /admin/providers/{id}/apikey` → stored in `AiProviders.ApiKey` |

---

## 19. Admin API Reference

All admin endpoints are under `/admin` and currently bypass API key auth (Phase 1).

### 19.1 API Keys

```bash
# Create API key
curl -X POST http://localhost:5011/admin/api-keys \
  -H "Content-Type: application/json" \
  -d '{"name": "my-key", "modelIds": []}'
# Response: { id, name, plainTextKey, message }

# List all keys
curl http://localhost:5011/admin/api-keys
# Response: [{ id, name, prefix, isActive, createdAt, expiresAt, allowedModelIds }]

# Toggle active/inactive
curl -X PUT http://localhost:5011/admin/api-keys/{id}/toggle

# Delete key
curl -X DELETE http://localhost:5011/admin/api-keys/{id}
```

### 19.2 Providers

```bash
# List providers
curl http://localhost:5011/admin/providers
# Response: [{ id, name, code, baseUrl, isEnabled, hasKey, ... }]

# Toggle provider
curl -X PUT http://localhost:5011/admin/providers/{id}/toggle

# Set provider API key
curl -X PUT http://localhost:5011/admin/providers/{id}/apikey \
  -H "Content-Type: application/json" \
  -d '{"apiKey": "sk-xxx"}'
```

### 19.3 Logs & Stats

```bash
# Recent usage logs
curl http://localhost:5011/admin/logs?count=50

# Aggregate stats
curl http://localhost:5011/admin/stats
# Response: { totalApiKeys, activeApiKeys, totalProviders, activeProviders,
#            todayRequests, todayTokens, todayCost }
```

---

## 20. Development Guide

### 20.1 Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- An IDE (VS 2025+, Rider, VS Code, or Cursor)

### 20.2 Run Locally (Full Stack)

```bash
# 1. Start infrastructure (PostgreSQL + Redis + n8n)
docker compose -f deploy/docker-compose.yml up -d

# 2. Apply migrations
dotnet ef database update \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api

# 3. Run gateway
dotnet run --project src/Arkana.Gateway.Api
# → Dashboard at http://localhost:5011
```

### 20.3 Run Locally (Gateway Only — no Docker)

For quick testing without Docker, you can:
- Use an existing PostgreSQL instance
- Or run with the in-memory tracking fallback

```bash
# Override connection string
ConnectionStrings__Postgres="Host=...;..." dotnet run --project src/Arkana.Gateway.Api
```

### 20.4 Watch Mode (Hot Reload)

```bash
dotnet watch run --project src/Arkana.Gateway.Api
```

### 20.5 Build

```bash
dotnet build                # Build all projects
dotnet build --no-restore   # Skip restore (faster after first build)
```

### 20.6 Test

```bash
dotnet test                 # Run all tests
dotnet test --no-build      # Skip build (faster after building)
```

### 20.7 Docker Build

```bash
# Build image
docker build -t ai-gateway .

# Build via Docker Compose
docker compose -f deploy/docker-compose.yml build --no-cache gateway

# Full stack restart
docker compose -f deploy/docker-compose.yml up -d --force-recreate gateway
```

---

## 21. Deployment Guide

### 21.1 Docker Compose (Production)

```yaml
# docker-compose.yml (same as deploy/docker-compose.yml)
# Add environment-specific .env file:
#   N8N_USER_EMAIL=admin@example.com
#   N8N_USER_PASSWORD=secure-password
#   N8N_ENCRYPTION_KEY=your-encryption-key

docker compose up -d
```

### 21.2 Setting Up Provider API Keys

After first startup, set the OpenCode API key via admin API:

```bash
curl -X PUT http://localhost:5011/admin/providers/a1000000-0000-0000-0000-000000000001/apikey \
  -H "Content-Type: application/json" \
  -d '{"apiKey": "sk-xxx"}'
```

Or set the `OPENCODE_GO_API_KEY` env var before first startup to use the bootstrap service.

### 21.3 Creating Gateway API Keys

```bash
curl -X POST http://localhost:5011/admin/api-keys \
  -H "Content-Type: application/json" \
  -d '{"name": "my-key", "modelIds": []}'
# Save the plainTextKey — it won't be shown again!
```

### 21.4 Health Checks

Docker health check hits `http://localhost:5011/` every 30s.
Custom health endpoint: `GET /health`

---

## 22. Troubleshooting

### 22.1 Streams Dashboard Shows 0 Active Streams

**Cause:** `ActiveStreamCounter.Increment()` is called in `ChatEndpoints` streaming path. If decremented too early or the try/finally block isn't reached, the counter stays 0.

**Fix:** Ensure `streamCounter.Decrement()` is in a `finally` block in both ChatEndpoints and ResponsesEndpoints streaming paths.

### 22.2 Dashboard Shows "Only Navigation Works"

**Cause:** Docker build used `--no-restore` which skips NuGet static web asset resolution. Blazor's `blazor.server.js` is not included in the published output.

**Fix:** ✅ Removed `--no-restore` from Dockerfile in commit `d18d85a`. Rebuild with `docker compose build --no-cache gateway`.

### 22.3 Streaming Returns 401 from Upstream

**Cause (fixed):** `appsettings.json` had `"ApiKey": ""` (empty string). The streaming path used `??` which only falls through on `null`, not `""`. So the empty string was used as the API key.

**Fix:** ✅ Changed to `string.IsNullOrEmpty()` logic in both ChatEndpoints and ResponsesEndpoints. The gateway now correctly falls back to DB query or env var.

### 22.4 Agent Mode Returns 403/1010

**Cause:** opencode.ai's security policy blocks agent-mode tool definitions.

**Fix:** Route to a different upstream provider (OpenAI, Anthropic) that accepts tool definitions. See [docs/tool-calls-and-ai-gateway.md](docs/tool-calls-and-ai-gateway.md).

### 22.5 Codex Desktop "Worked for 0s"

**Cause:** Codex Desktop's sandbox blocks file access outside permitted roots. This is NOT a gateway issue.

**Fix:** File → Open Folder in Codex Desktop to grant sandbox permission.

### 22.6 API Keys Wiped on Container Restart

**Cause:** Docker volumes may be recreated or not persisted.

**Fix:** Ensure `pgdata` volume is properly mounted. Keys are stored in PostgreSQL `ApiKeys` table — if the DB is persistent, keys survive restarts. Only the env-var bootstrap flow (`OPENCODE_GO_API_KEY`) runs on every startup, but it only writes the key if the DB record has no key yet.

---

## 23. Appendix: All Code Files & Their Roles

### 23.1 Domain Layer (`src/Arkana.Domain/`)

| File | Role |
|------|------|
| `Entities/AiProvider.cs` | AI provider entity with credentials, pricing, enable/disable |
| `Entities/ApiKey.cs` | Gateway API key entity with hashing, permissions, expiry |
| `Entities/Model.cs` | AI model entity with pricing, provider FK |
| `Interfaces/IAiProviderRepository.cs` | Repository contract for AI providers |
| `Interfaces/IApiKeyRepository.cs` | Repository contract for API keys |
| `Interfaces/IChatCompletionService.cs` | Chat service contract + ChatRequest/ChatResult DTOs |
| `Interfaces/IModelRepository.cs` | Repository contract for models |
| `Interfaces/IModelRouter.cs` | Provider selection/failover contract |
| `Interfaces/IN8nService.cs` | n8n workflow integration contract |
| `Interfaces/IRequestLogger.cs` | Full request/response logging contract |
| `Interfaces/ITokenTracker.cs` | Token usage tracking contract |
| `Models/WorkflowInfo.cs` | n8n workflow DTO |
| `Services/ApiKeyHasher.cs` | SHA-256 hashing + key generation |
| `ValueObjects/RequestLog.cs` | Full request/response log record |
| `ValueObjects/TokenUsage.cs` | Token usage record |
| `ValueObjects/ToolCallInfo.cs` | Tool call result DTO |

### 23.2 Application Layer (`src/Arkana.Application/`)

| File | Role |
|------|------|
| `Common/Models/Result.cs` | Generic operation result |
| `DependencyInjection.cs` | MediatR + validator registration |
| `Features/Admin/Commands/CreateApiKeyCommand.cs` | Create API key command |
| `Features/Chat/Commands/SendChatCommand.cs` | Chat request CQRS command + DTOs |
| `Features/Chat/Commands/SendChatCommandValidator.cs` | FluentValidation rules |
| `Features/Chat/Handlers/SendChatHandler.cs` | Chat execution: route, validate, execute, log |
| `Features/Chat/Handlers/GetTokenUsageHandler.cs` | Usage retrieval handler |
| `Features/Chat/Queries/GetTokenUsageQuery.cs` | Usage query DTO |

### 23.3 Infrastructure Layer (`src/Arkana.Infrastructure/`)

| File | Role |
|------|------|
| `AI/CLIProxyAPIChatService.cs` | CLIProxyAPI provider (for subscription CLI tools) |
| `AI/ModelRouter.cs` | Provider selection with priority order |
| `AI/OpenAIChatService.cs` | OpenAI provider (P1 fallback) |
| `AI/OpenCodeChatService.cs` | OpenCode provider (P0 primary) |
| `DependencyInjection.cs` | EF Core, repos, AI clients, services registration |
| `Migrations/*.cs` | 4 EF Core migrations |
| `Persistence/Entities/TokenUsageEntity.cs` | EF entity for TokenUsages table |
| `Persistence/Entities/RequestLogEntity.cs` | EF entity for RequestLogs table (JSONB) |
| `Persistence/GatewayDbContext.cs` | DbContext with 5 DbSets + seed data |
| `Persistence/Repositories/AiProviderRepository.cs` | AiProvider EF Core repository |
| `Persistence/Repositories/ApiKeyRepository.cs` | ApiKey EF Core repository |
| `Persistence/Repositories/ModelRepository.cs` | Model EF Core repository |
| `Services/ApiKeyBootstrapService.cs` | Startup: seed env var key into DB |
| `Services/EfCoreRequestLogger.cs` | Persistent request logging |
| `Services/EfCoreTokenTracker.cs` | Persistent token tracking |
| `Services/InMemoryTokenTracker.cs` | Dev-only in-memory fallback |
| `Services/N8nOptions.cs` | n8n config POCO |
| `Services/N8nService.cs` | n8n REST HTTP client |

### 23.4 Gateway API Layer (`src/Arkana.Gateway.Api/`)

| File | Role |
|------|------|
| `Program.cs` | App bootstrap, middleware pipeline, endpoint mapping |
| `Endpoints/AdminEndpoints.cs` | /admin/* route handlers |
| `Endpoints/ChatEndpoints.cs` | /v1/chat/completions (streaming + non-streaming) |
| `Endpoints/ResponsesEndpoints.cs` | /v1/responses (Responses API translation) |
| `Middleware/ApiKeyAuthMiddleware.cs` | API key validation middleware |
| `Middleware/TokenTrackingMiddleware.cs` | Request timing/stats middleware |
| `Services/ActiveStreamCounter.cs` | Thread-safe SSE connection counter |
| `Services/DashboardService.cs` | Blazor dashboard data service |
| `Services/UserTimeService.cs` | Timezone-aware time display |
| `Components/App.razor` | Blazor root component |
| `Components/Layout/MainLayout.razor` | Dashboard layout |
| `Components/Layout/NavMenu.razor` | Sidebar navigation |
| `Components/Layout/UserTimeDisplay.razor` | Clock widget |
| `Components/Pages/Dashboard.razor` | Main dashboard page |
| `Components/Pages/Logs.razor` | Request logs page |
| `Components/Pages/ApiKeys.razor` | API key management page |
| `Components/Pages/Providers.razor` | Provider management page |
| `Components/Pages/Cost.razor` | Cost analytics page |
| `Components/Shared/CostChart.razor` | CSS cost chart component |
| `Components/Shared/UsageChart.razor` | CSS usage chart component |
| `Components/Shared/StatusBadge.razor` | Status indicator component |

### 23.5 Other Projects

| File | Project | Role |
|------|---------|------|
| `ServiceDefaults/Extensions.cs` | Arkana.ServiceDefaults | Health checks, telemetry, defaults |
| `ServiceDefaults/EndpointExtensions.cs` | Arkana.ServiceDefaults | Default endpoint mappings |
| `AppHost/AppHost.cs` | Arkana.AppHost | Aspire orchestration |

---

> **Document version:** 1.0  
> **Author:** Hermes Agent (ARKANA GATEWAY)  
> **Related docs:** `docs/tool-calls-and-ai-gateway.md`, `docs/architecture/opencode-integration-architecture.md`  
> **Source:** [https://github.com/hernanda-git/arkana-gateway](https://github.com/hernanda-git/arkana-gateway)
