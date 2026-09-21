# Documentation

> For the full project overview, see the [root README](../README.md).
>
> **Canonical Agent rollout docs:** start with [`codex-gateway-installation.md`](codex-gateway-installation.md), then use [`agent-gateway-compatibility.md`](agent-gateway-compatibility.md) and [`agent-setup-prompt.md`](agent-setup-prompt.md). These documents reflect the verified production path at `https://gateway.arkana.dev/v1`.

| Directory / File | Contents |
|-----------|----------|
| [`01-Project-Overview/`](01-Project-Overview/) | Vision, roadmap phases, goals, success metrics |
| [`02-Technology-Stack/`](02-Technology-Stack/) | Tech decisions — .NET, Python, databases, infrastructure |
| [`03-Architecture/`](03-Architecture/) | System architecture, component design, data flow, ADRs |
| [`04-Implementation-Phases/`](04-Implementation-Phases/) | Phase 1–6 detailed implementation plans |
| [`05-DotNet-Capabilities/`](05-DotNet-Capabilities/) | Deep knowledge: .NET 10, Semantic Kernel, Aspire, SignalR, Blazor, EF Core |
| [`06-Python-Integration/`](06-Python-Integration/) | Python interop strategies — Python.NET, gRPC, process bridging |
| [`07-Reference/`](07-Reference/) | Links, glossary, tooling guides, templates |
| [`codex-setup-from-scratch.md`](codex-setup-from-scratch.md) | **End-to-end: gateway + Codex (Responses API) from zero** |
| [`agent-cli-integration-setup.md`](agent-cli-integration-setup.md) | OpenCode / OpenClaude / Codex CLI → gateway (side-by-side) |
| [`codex-gateway-employee-guide.md`](codex-gateway-employee-guide.md) | Employee Codex setup + troubleshooting (LAN adapter) |
| [`agent-provider-guide.md`](agent-provider-guide.md) | Configure any AI agent as an OpenAI-compatible client |
| [`codex-gateway-installation.md`](codex-gateway-installation.md) | **Canonical production Codex CLI/Desktop setup and real Tool-loop verification** |
| [`agent-gateway-compatibility.md`](agent-gateway-compatibility.md) | Verified/conditional/not-direct matrix for major Agents |
| [`agent-setup-prompt.md`](agent-setup-prompt.md) | Copy-paste prompt for an Agent to install and verify the Gateway Provider |
| [`verification-codex-complex-tool-loop-2026-08-21.md`](verification-codex-complex-tool-loop-2026-08-21.md) | Actual production complex Tool-loop report |
| [`../scripts/setup-codex-gateway.ps1`](../scripts/setup-codex-gateway.ps1) | Secret-safe Windows Codex Provider setup script |
| [`../scripts/setup-codex-gateway.sh`](../scripts/setup-codex-gateway.sh) | Secret-safe macOS/Linux Codex Provider setup script |
| [`key-management.md`](key-management.md) | Deployment & API-key / envelope-encryption management |
| [`docker-deployment.md`](docker-deployment.md) | Docker stack: dev mode + full container mode |
| [`deployment-operations.md`](deployment-operations.md) | gateway-host deploy runbook (full-tree tar, verify, hygiene) |
| [`release-reconciliation-20260901.md`](release-reconciliation-20260901.md) | **Authoritative release record:** incident, selective Gemini integration, architecture, gates, evidence, and final state |
| [`release-reconciliation-20260901-runbook.md`](release-reconciliation-20260901-runbook.md) | **Canonical gated promotion procedure:** source transfer, immutable image approval, gateway-only deploy, postflight, and rollback |
| [`GATEWAY-STATUS-REPORT-2026-08-28.md`](GATEWAY-STATUS-REPORT-2026-08-28.md) | Historical status: production evidence, workstream reconciliation, and publication gate |
| [`LESSONS_LEARNED.md`](LESSONS_LEARNED.md) | Living operational lessons, pitfalls, and verified recovery patterns |
| [`GATEWAY-STATUS-REPORT-2026-08-26.md`](GATEWAY-STATUS-REPORT-2026-08-26.md) | Historical status: final reconciliation, 907 tests, single-branch state |
| [`session-context-arkana-gateway-20260826.md`](session-context-arkana-gateway-20260826.md) | **New-session primer:** read this first before any work |
| [`profile-page-audit-20260826.md`](profile-page-audit-20260826.md) | Historical profile-page audit and verification notes |
| [`CI-CD.md`](CI-CD.md) | Branch protection & required status checks |
| [`tool-calls-and-ai-gateway.md`](tool-calls-and-ai-gateway.md) | Agent tool-calling analysis (native vs gateway-routed) |
| [`antigravity-integration.md`](antigravity-integration.md) | Antigravity integration reference and historical operational notes |

### MITM / Antigravity routing
Opt-in, per-employee local agent that routes an employee's **Antigravity (Gemini IDE agent)**
traffic through the gateway on gateway-host. Use [`antigravity-integration.md`](antigravity-integration.md)
for the current reference and verify the actual client version before setup.
In the dashboard **Logs** page, use the **"Via"** filter (All Routes / Routed via MITM /
Direct) to isolate Antigravity-routed traffic at a glance.
