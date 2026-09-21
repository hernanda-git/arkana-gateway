# ADR-002: Semantic Kernel as AI Middleware

**Status:** Proposed
**Date:** 2026-06-06

## Context
We need an AI middleware layer that supports plugins, planners, memory, agent orchestration, and multi-provider routing — without vendor lock-in.

## Decision
Use Semantic Kernel (SK) as the core AI middleware, with Microsoft.Extensions.AI abstractions for multi-provider support.

## Rationale
- Enterprise-grade with telemetry, hooks, filters
- Multi-language (C#, Python, Java) — Python SK can share same patterns
- Plugins map naturally to agent capabilities
- Process Framework for complex workflows
- Agent Framework for multi-agent coordination
- Vector store integrations for RAG

## Consequences
- All AI interactions go through SK Kernel
- Plugins define agent capabilities
- Filter system handles cross-cutting concerns (cost, audit)
- Agent Framework used for multi-agent orchestration
