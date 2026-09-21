# ADR-001: Use .NET 10 as Primary Runtime

**Status:** Proposed
**Date:** 2026-06-06

## Context
We need a mature, high-performance runtime for the AI Gateway that supports Native AOT, modern C# features, and enterprise-grade tooling.

## Decision
Use .NET 10 (LTS, shipping ~November 2026) as the primary runtime.

## Rationale
- LTS release with 3-year support window
- Native AOT for cold-start critical gateway services
- AVX-512 vectorization for AI/token workloads
- Microsoft.Extensions.AI unified AI abstractions
- Deep Semantic Kernel integration
- .NET Aspire for distributed orchestration

## Consequences
- Must target .NET 10 SDK for development
- Can leverage C# 14 features
- Python interop via gRPC bridge process (not in-proc)
- Early builds can start with .NET 9 preview for experimentation
