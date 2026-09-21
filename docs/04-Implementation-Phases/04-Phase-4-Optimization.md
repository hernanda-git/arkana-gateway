# Phase 4 — Optimization

## Goal
Optimize token consumption, cost, and performance.

## Model Routing Strategy

```mermaid
flowchart LR
    REQ["Request"] --> ROUTER["Model Router"]
    ROUTER --> CLASSIFY["Classify Task Type"]
    CLASSIFY -->|Simple/Internal| INTERNAL["Internal Model<br/>OpenCode"]
    CLASSIFY -->|Structured| CHEAP["Cheapest External<br/>Gemini Flash / GPT-4o-mini"]
    CLASSIFY -->|Complex/Reasoning| POWERFUL["Powerful Model<br/>GPT-4o / Claude Sonnet"]
    CLASSIFY -->|Creative/Code| CREATIVE["Creative Model<br/>Claude Opus / GPT-4o"]
    CLASSIFY -->|Local/Private| LOCAL["Local Model<br/>Ollama / Llama"]
    INTERNAL --> RESP["Response"]
    CHEAP --> RESP
    POWERFUL --> RESP
    CREATIVE --> RESP
    LOCAL --> RESP
```

## Cost Optimization Features

| Feature | Description | Est. Savings |
|---------|-------------|:------------:|
| **Model Routing** | Route to cheapest adequate model | 20-30% |
| **Response Caching** | Cache identical requests | 15-25% |
| **Prompt Compression** | Reduce prompt size via summarization | 10-20% |
| **Token Budgeting** | Per-user/project token caps | Prevents runaway costs |
| **Batch Processing** | Batch similar requests | 5-10% |

## Tasks

- [ ] Implement model routing engine (cost/quality optimization)
- [ ] Response cache (Redis, with TTL and invalidation)
- [ ] Real-time cost tracking middleware
- [ ] Per-user/project token budgets
- [ ] Rate limiting (sliding window, token bucket)
- [ ] Admin API for cost reports
- [ ] Usage projections and anomaly detection
