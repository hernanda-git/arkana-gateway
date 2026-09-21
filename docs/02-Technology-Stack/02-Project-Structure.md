# Project Structure (Monorepo)

```
ai-gateway/
├── src/
│   ├── Arkana.Gateway.Api/           # AI Gateway — ASP.NET Core Minimal API
│   ├── Arkana.Gateway.Core/          # Domain logic, models, interfaces
│   ├── Arkana.Gateway.Infrastructure/ # EF Core, Redis, external providers
│   ├── Arkana.Agent.Runtime/         # Agent execution engine
│   ├── Arkana.Workflow.Engine/       # Workflow orchestration (SK Process Framework)
│   ├── Arkana.Python.Bridge/         # gRPC server for Python interop
│   ├── Arkana.Admin.Web/            # Blazor Server admin dashboard
│   └── Arkana.Aspire.AppHost/       # .NET Aspire orchestration host
├── src-python/
│   ├── arkana_agents/               # Python agent implementations
│   ├── arkana_ml/                   # ML model serving
│   └── arkana_bridge/              # gRPC client for .NET bridge
├── tests/
│   ├── Arkana.Gateway.Tests/
│   ├── Arkana.Agent.Tests/
│   └── Arkana.Integration.Tests/
├── Docs/                              # This documentation
├── deploy/                            # Docker, Helm, compose files
├── .github/                           # CI/CD workflows
├── Arkana.sln                       # .NET solution file
└── README.md
```
