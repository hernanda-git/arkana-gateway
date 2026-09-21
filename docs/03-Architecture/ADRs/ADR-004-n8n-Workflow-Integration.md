# ADR-004: n8n Workflow Integration via REST API

**Status:** Approved
**Date:** 2026-06-07

## Context

The AI Gateway needs a workflow automation system that enables users to visually design, schedule, and trigger multi-step processes that involve AI calls, data transformations, and external integrations. Requirements:

- Visual workflow builder (not hand-coded)
- Multiple trigger types: manual, cron/scheduled, webhook/event-based
- Integration with the Gateway's AI routing capabilities
- Runs alongside the existing Docker infrastructure (PostgreSQL 16 + Redis 7)
- Listed and manageable from within the Gateway's Blazor dashboard

## Decision

Use **n8n** (self-hosted Docker container) as the workflow automation engine, and integrate it into the Gateway Blazor dashboard via **n8n's REST API**.

### Architecture

```
┌─────────────────────────────────────────┐
│  Docker Compose (arkana-dev)          │
│                                          │
│  ┌──────────┐  ┌───────┐  ┌────────┐   │
│  │PostgreSQL│  │ Redis │  │  n8n   │    │
│  │   :5432  │  │:6379  │  │ :5678  │    │
│  └────┬─────┘  └───────┘  └───┬────┘    │
│       │                        │         │
│       │ EF Core                │ HTTP    │
│       ▼                        ▼         │
│  ┌──────────────────────────────────┐    │
│  │ AI Gateway (:5011)               │    │
│  │  - Blazor Server Dashboard      │    │
│  │  - IN8nService → N8nService     │    │
│  │  - /workflows, /workflows/{id}  │    │
│  └──────────────────────────────────┘    │
└─────────────────────────────────────────┘
```

### Key Points

| Aspect | Decision |
|--------|----------|
| **Engine** | n8n (latest) in Docker |
| **Integration** | n8n REST API via `IN8nService` interface |
| **Data store** | Shared PostgreSQL (`arkana` DB, `n8n` schema) |
| **Authentication** | n8n API key (`X-N8N-API-KEY` header) |
| **Scheduling** | n8n native cron trigger nodes (read-only in dashboard) |
| **Dashboard pages** | `/workflows` (list) + `/workflows/{id}` (detail + executions) |
| **MCP/ACP** | Deferred — REST API only for now |

### n8n REST API Endpoints Used

| Gateway Operation | n8n API Endpoint |
|---|---|
| List workflows | `GET /rest/workflows` |
| Get workflow detail | `GET /rest/workflows/{id}` |
| Execute workflow | `POST /rest/workflows/{id}/execute` |
| Activate workflow | `POST /rest/workflows/{id}/activate` |
| Deactivate workflow | `POST /rest/workflows/{id}/deactivate` |
| List executions | `GET /rest/executions` |
| Get execution detail | `GET /rest/executions/{id}` |

### Trigger Type Detection

Workflow trigger type is detected at runtime by inspecting the workflow's node types:

| Node Type | Trigger Type |
|---|---|
| `scheduleTrigger` / `cron` | Cron |
| `webhook` / `formTrigger` | Webhook |
| `manualTrigger` | Manual |
| Multiple trigger types | Mixed |

## Rationale

1. **n8n over alternatives**: n8n is the most mature self-hosted visual workflow engine with a clean REST API, active community, and native PostgreSQL support.
2. **REST over MCP/ACP**: Simpler to implement and maintain. The existing Phase 5 MCP roadmap can wrap the REST layer later when MCP support is needed.
3. **Shared PostgreSQL**: Reduces operational complexity — one database to backup, monitor, and manage. The `n8n` schema keeps workflow data isolated from gateway data.
4. **Dashboard integration**: Users manage workflows alongside providers, API keys, and logs without switching contexts.

## Consequences

- **Positive**: No additional database infrastructure; workflows appear natively in the Gateway dashboard; users can build complex automations visually in n8n's editor.
- **Negative**: Gateway availability depends on n8n container being healthy; initial n8n setup requires generating an encryption key and API key.
- **Neutral**: n8n editor is a separate application at `http://localhost:5678` — users must navigate there for workflow authoring; the Gateway dashboard only lists, triggers, and monitors.

## Setup Steps

1. Copy `.env.example` to `.env` and set `N8N_ENCRYPTION_KEY` (generate via `openssl rand -hex 32`)
2. Run `docker compose -f deploy/docker-compose.yml up -d` — starts PostgreSQL, Redis, and n8n
3. Open n8n editor at `http://localhost:5678`, create admin user, generate an API key
4. Set `N8N_API_KEY` in `.env` or `appsettings.json`
5. Run the Gateway — the `/workflows` page reads from n8n automatically

## References

- [n8n REST API Documentation](https://docs.n8n.io/api/)
- [n8n Docker Setup](https://docs.n8n.io/hosting/installation/docker/)
- [Phase 5 — MCP & Webhook](../../04-Implementation-Phases/05-Phase-5-MCP-Webhook.md)
