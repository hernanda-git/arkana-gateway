# System Architecture

## High-Level Architecture

```mermaid
flowchart TB
    subgraph Clients["Clients"]
        APP["App / Website"]
        CLI["CLI / Agent SDK"]
        WEB["Webhook Receiver"]
    end

    subgraph Gateway["AI Gateway Layer"]
        LB["Load Balancer"]
        GW["AI Gateway API<br/>ASP.NET Core Minimal API"]
        AUTH["Auth Middleware<br/>JWT / API Key"]
        RATE["Rate Limiter"]
        ROUTE["Model Router"]
        AUDIT["Audit Logger"]
        CACHE["Response Cache<br/>Redis"]
    end

    subgraph Core["Core Engine"]
        SK["Semantic Kernel<br/>Kernel Builder"]
        PLUGINS["Plugin Registry"]
        MEMORY["Semantic Memory<br/>Vector Store"]
        PLANNER["Function Planner"]
    end

    subgraph Agents["Agent Hub"]
        AGT_QA["QA Agent"]
        AGT_DEV["Dev Agent"]
        AGT_ANALYST["Analyst Agent"]
        AGT_REVIEW["Review Agent"]
        AGT_WF["Workflow Agent"]
    end

    subgraph Workflows["Workflow Hub"]
        WF_ENG["Workflow Engine<br/>SK Process Framework"]
        MCP_SRV["MCP Server"]
        MCP_CLI["MCP Client"]
        HOOKS["Webhook Dispatcher"]
        QUEUE["Message Queue<br/>RabbitMQ"]
    end

    subgraph AI["AI Providers"]
        OPENCODE["OpenCode"]
        OPENAI["OpenAI"]
        GEMINI["Gemini"]
        CLAUDE["Anthropic"]
        OLLAMA["Ollama"]
    end


    subgraph Data["Persistence"]
        PG[("PostgreSQL<br/>EF Core")]
        REDIS[("Redis<br/>Cache + Sessions")]
        VECTOR[("Vector DB<br/>pgvector / Qdrant")]
    end

    subgraph Python["Python Bridge"]
        GRPC["gRPC Server"]
        PY_AGENTS["Python Agents"]
        ML_MODELS["ML Models"]
    end

    Clients --> LB --> GW
    GW --> AUTH --> RATE --> ROUTE
    ROUTE --> CACHE
    CACHE --> SK
    SK --> PLUGINS --> AGENTS
    SK --> WORKFLOWS
    AGENTS --> AI
    WORKFLOWS --> AI
    ROUTE --> AUDIT
    GW --> QUEUE
    QUEUE --> WF_ENG
    SK --> MEMORY --> VECTOR
    SK --> PLANNER
    AI --> |gRPC| Python
    Python --> PY_AGENTS
    Python --> ML_MODELS
    GW -.-> Data
    SK -.-> Data
```

## Request Flow

1. **Client** sends request with API key / JWT
2. **Gateway** authenticates & authorizes
3. **Rate Limiter** checks quota
4. **Model Router** selects optimal model/provider
5. **Cache** checks for cached response
6. **Semantic Kernel** processes with plugins
7. **Agent/Workflow** executes business logic
8. **Response** returned with telemetry
