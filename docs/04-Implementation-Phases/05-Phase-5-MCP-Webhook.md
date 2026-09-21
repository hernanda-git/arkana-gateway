# Phase 5 — MCP & Webhook

## Status: ✅ COMPLETE

## Goal
Support the Model Context Protocol (MCP) and external integration hooks.

## Delivered

### MCP Server (Phase 5 kickoff)
- `POST /mcp/` — JSON-RPC 2.0 stateless endpoint
- Methods: `initialize`, `tools/list`, `tools/call`, `resources/list`
- Tools: `list_models`, `generate_text`, `chat`, `get_usage`
- Resources: `gateway://models`, `gateway://usage`, `gateway://templates`
- Full test coverage (12 tests)

### MCP Client
- HTTP+SSE transport with JSON-RPC 2.0 wire protocol
- `IMcpClient` interface: `ConnectAsync`, `ListToolsAsync`, `CallToolAsync`, `ListResourcesAsync`, `ReadResourceAsync`
- `McpClientOptions`: configurable URL, timeout, client info
- `McpRpcException` for structured error handling
- Unit tests with NSubstitute mocks

### Webhook Engine
- **Entity:** `Webhook` — tenant-scoped, URL + secret + events array
- **Repository:** `IWebhookRepository` with full CRUD
- **API:** `GET/POST/PUT/DELETE /admin/webhooks`
- **Dispatcher:** `WebhookDispatcher` — background `Channel<T>` queue processor
  - Exponential backoff retry (configurable max retries)
  - HMAC-SHA256 payload signatures (`X-Webhook-Signature` header)
  - Idempotency via `X-Webhook-Event` + `X-Webhook-Delivery` headers
- **Events:** `agent.completed`, `workflow.completed`, `token.threshold`, `error.rate`

## Files Created
- `src/Arkana.Infrastructure/Mcp/McpClient.cs`
- `src/Arkana.Infrastructure/Mcp/McpClientOptions.cs`
- `src/Arkana.Domain/Interfaces/IMcpClient.cs`
- `src/Arkana.Domain/Entities/Webhook.cs`
- `src/Arkana.Domain/Interfaces/IWebhookRepository.cs`
- `src/Arkana.Infrastructure/Persistence/Repositories/WebhookRepository.cs`
- `src/Arkana.Gateway.Api/Endpoints/WebhookEndpoints.cs`
- `src/Arkana.Gateway.Api/Services/WebhookDispatcher.cs`
