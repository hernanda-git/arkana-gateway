# .NET Aspire for Distributed Orchestration

## Why Aspire for AI Gateway?

Aspire provides a **code-first orchestration model** perfect for AI Gateway's multi-service architecture.

### AppHost Definition

```csharp
// Arkana.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache")
    .WithDataVolume("arkana-cache-data");

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("arkana-db-data");
var db = postgres.AddDatabase("arkanadb");

var queue = builder.AddRabbitMQ("queue");

var gateway = builder.AddProject<Projects.Arkana_Gateway_Api>("gateway")
    .WithReference(cache)
    .WithReference(db)
    .WithReference(queue)
    .WithReplicas(2);

var adminUi = builder.AddProject<Projects.Arkana_Admin_Web>("admin-ui")
    .WithReference(gateway)
    .WithExternalHttpEndpoints();

var pythonBridge = builder.AddContainer("python-bridge", "arkana-python")
    .WithBindMount("../src-python", "/app")
    .WithReference(queue);

builder.Build().Run();
```

### Key Benefits

| Benefit | Description |
|---------|-------------|
| **Single Command** | `aspire run` starts all 5+ services |
| **Dashboard** | OpenTelemetry traces, logs, metrics in one place |
| **Service Discovery** | Services find each other by name |
| **Replicas** | Scale individual services independently |
| **Environment Config** | Development vs production profiles |
| **Container Orchestration** | Docker containers for Python services |

### Services Managed by Aspire

| Service | Technology | Managed As |
|---------|-----------|------------|
| Gateway API | ASP.NET Core 10 | .NET Project |
| Admin UI | Blazor Server | .NET Project |
| Agent Runtime | .NET Console | .NET Project |
| Python Bridge | Python (gRPC) | Container |
| PostgreSQL | Database | Container |
| Redis | Cache | Container |
| RabbitMQ | Message Queue | Container |
