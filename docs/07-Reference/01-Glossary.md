# Glossary

| Term | Definition |
|------|------------|
| **Agent** | Autonomous AI entity with specific capabilities, goals, and tools |
| **MCP (Model Context Protocol)** | Open protocol standardizing how AI models connect to external tools and data sources |
| **Semantic Kernel** | Microsoft's open-source AI orchestration SDK (C#, Python, Java) |
| **Aspire** | .NET code-first orchestration and observability layer for distributed apps |
| **Microsoft.Extensions.AI** | Unified AI client abstractions (`IChatClient`, `IEmbeddingGenerator`) with middleware pipeline |
| **Model Router** | Component that selects optimal AI model per request based on cost, latency, capability |
| **Plugin** | Collection of functions exposed to AI for automatic calling via `[KernelFunction]` |
| **Workflow** | Multi-step process orchestrated by the AI engine (SK Process Framework) |
| **Token** | Unit of text (character/subword) used for LLM input/output and pricing |
| **Native AOT** | Ahead-of-time compilation to native binary (no JIT) — faster startup, lower memory |
| **OpenTelemetry** | CNCF observability framework for traces, metrics, logs across services |
| **gRPC** | High-performance RPC framework using Protocol Buffers, supports bidirectional streaming |
| **SignalR** | ASP.NET Core library for real-time web connections (WebSocket, SSE, long polling) |
| **Blazor Server** | .NET UI framework rendering on server with real-time SignalR connection to browser |
| **MudBlazor** | Material Design component library for Blazor |
| **PGO** | Profile-Guided Optimization — JIT optimization based on runtime profiling data |
| **SK Agent Framework** | Multi-agent coordination: `ChatCompletionAgent`, `AgentGroupChat` |
| **SK Process Framework** | Stateful step-based process engine (PULSE pattern) |
| **ADRs** | Architecture Decision Records — documents capturing architectural decisions and rationale |
| **Ollama** | Local LLM runner for open-source models (Llama, Mistral, Phi, etc.) |
