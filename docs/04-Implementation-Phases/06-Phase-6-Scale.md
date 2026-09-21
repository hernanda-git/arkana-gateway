# Phase 6 — Scale

## Status: ✅ COMPLETE

## Goal
Full multi-agent orchestration, SLA monitoring, and AI economy readiness.

## Delivered

### Multi-Agent Orchestration

#### Agent Definitions
- `AgentDefinition` entity: name, description, system prompt, model code, temperature, max tokens
- Tenant-scoped with activate/deactivate support
- CRUD API: `GET/POST/PUT/DELETE /agents`

#### Agent Tasks
- `AgentTask` entity: input, output, status (Pending/Running/Completed/Failed/Cancelled)
- Tracks latency, token usage, error messages
- Parent-child delegation chains for multi-step workflows
- Status API: `GET /agents/{id}/tasks`, `GET /agents/tasks/{taskId}`

#### Agent Orchestrator
- `AgentOrchestrator` service — runs tasks via the gateway's chat completion pipeline
- Resolves agent's model code to provider connector
- Handles timeouts and error recovery
- `DelegateAsync` — creates child tasks for multi-agent workflows
- API: `POST /agents/{id}/run`, `POST /agents/tasks/{id}/delegate`

### SLA Monitoring

#### SlaMetric Entity
- Tracks per provider/model: latency (avg, p50, p95, p99, max), error rate, uptime
- Rolling window metrics with consecutive failure tracking
- Auto-detects degradation (error rate > 10% or 5+ consecutive failures)

#### SlaMonitor Service
- `ISlaMetricsRecorder` interface for request pipeline integration
- Records success/failure with latency measurements
- Queries unhealthy providers/models

#### SLA API
- `GET /admin/sla` — list all metrics for tenant
- `GET /admin/sla/unhealthy` — degraded providers/models
- `GET /admin/sla/{provider}/{model}` — specific metric

## Files Created
- `src/Arkana.Domain/Entities/AgentDefinition.cs`
- `src/Arkana.Domain/Entities/AgentTask.cs`
- `src/Arkana.Domain/Entities/AgentTaskStatus.cs`
- `src/Arkana.Domain/Entities/SlaMetric.cs`
- `src/Arkana.Domain/Interfaces/IAgentRepository.cs`
- `src/Arkana.Domain/Interfaces/ISlaRepository.cs`
- `src/Arkana.Infrastructure/Persistence/Repositories/AgentRepository.cs`
- `src/Arkana.Infrastructure/Persistence/Repositories/SlaRepository.cs`
- `src/Arkana.Gateway.Api/Services/AgentOrchestrator.cs`
- `src/Arkana.Gateway.Api/Services/SlaMonitor.cs`
- `src/Arkana.Gateway.Api/Endpoints/AgentEndpoints.cs`
- `src/Arkana.Gateway.Api/Endpoints/SlaEndpoints.cs`
- `tests/Arkana.Domain.Tests/Entities/SlaMetricTests.cs`
- `tests/Arkana.Gateway.Api.Tests/Endpoints/AgentEndpointsTests.cs`
