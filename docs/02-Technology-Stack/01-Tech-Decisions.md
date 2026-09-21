# Technology Stack Decisions

## Core Runtime: .NET 10

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Runtime | .NET 10 LTS | Long-term support, Native AOT, latest perf |
| Language | C# 14 | Primary development language |
| ASP.NET Core | 10.0 | Minimal APIs, middleware pipeline, OpenAPI |

## AI & Agents

| Library | Purpose | Why |
|---------|---------|-----|
| **Semantic Kernel** | AI orchestration middleware | Enterprise-grade, plugins, planners, memories |
| **Microsoft.Extensions.AI** | Unified AI client abstraction | Standardized IChatClient/IEmbeddingGenerator |
| **SK Agent Framework** | Multi-agent coordination | Built on SK, supports agent collaboration |
| **SK Process Framework** | Step-based workflows | Stateful process orchestration |

## Infrastructure

| Component | Choice | Alternative |
|-----------|--------|-------------|
| Service Orchestration | .NET Aspire | Docker Compose, Kubernetes |
| Database | PostgreSQL + Redis | SQL Server (if existing infra) |
| Message Queue | RabbitMQ | Azure Service Bus |
| Containerization | Docker + Docker Compose | — |
| CI/CD | GitHub Actions | Azure DevOps |
| Monitoring | OpenTelemetry + Aspire Dashboard | Prometheus/Grafana |
