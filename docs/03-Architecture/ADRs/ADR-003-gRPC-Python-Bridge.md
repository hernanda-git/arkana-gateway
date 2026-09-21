# ADR-003: gRPC Bridge for Python Interop

**Status:** Proposed
**Date:** 2026-06-06

## Context
AI Gateway needs Python for ML/AI workloads, but the primary platform is .NET. We need a robust, performant interop mechanism.

## Decision
Use **gRPC Bridge** (separate Python process communicating via gRPC) as the primary Python integration strategy.

## Options Considered

| Option | Verdict | Reason |
|--------|---------|--------|
| Python.NET (pythonnet) | Rejected | GIL contention, DLL issues on Windows |
| Process Bridge (sidecar) | Rejected | No streaming, per-request overhead |
| REST API | Rejected | Text serialization, no streaming |
| **gRPC Bridge** | **Selected** | Type-safe, streaming, fast, mature |

## Consequences
- Separate Python process managed by Aspire as a container
- Protobuf contracts define all cross-language boundaries
- Bidirectional streaming for agent execution progress
- Python team owns the gRPC server implementation
