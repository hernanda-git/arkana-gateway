# Documentation — ARKANA GATEWAY

> Project overview lives in the [root README](../README.md). This page is the map of the
> documentation set: what exists, what it covers, and how fresh it is.

**Freshness legend**

| Label | Meaning |
|---|---|
| **ACTIVE** | Reflects the current source tree and is safe to rely on |
| **HISTORICAL** | Point-in-time record (incident, verification run, superseded plan). Useful for context, not as current behaviour |

## Start here

| Document | Freshness | Contents |
|---|---|---|
| [`codex-setup-from-scratch.md`](codex-setup-from-scratch.md) | ACTIVE | End-to-end: gateway + Codex (Responses API) from zero |
| [`agent-cli-integration-setup.md`](agent-cli-integration-setup.md) | ACTIVE | OpenCode / Codex CLI / other agents → gateway, side by side |
| [`codex-gateway-installation.md`](codex-gateway-installation.md) | ACTIVE | Canonical Codex CLI/Desktop setup and tool-loop verification |
| [`agent-gateway-compatibility.md`](agent-gateway-compatibility.md) | ACTIVE | Verified / conditional / not-direct matrix for major agents |
| [`agent-provider-guide.md`](agent-provider-guide.md) | ACTIVE | Configure any agent as an OpenAI-compatible client |
| [`agent-setup-prompt.md`](agent-setup-prompt.md) | ACTIVE | Copy-paste prompt an agent can use to install + verify the provider |
| [`../scripts/setup-codex-gateway.ps1`](../scripts/setup-codex-gateway.ps1) | ACTIVE | Secret-safe Windows Codex provider setup script |
| [`../scripts/setup-codex-gateway.sh`](../scripts/setup-codex-gateway.sh) | ACTIVE | Secret-safe macOS/Linux Codex provider setup script |

## Architecture and design

| Document | Freshness | Contents |
|---|---|---|
| [`01-Project-Overview/`](01-Project-Overview/) | ACTIVE | Vision, goals, roadmap phases, user roles |
| [`02-Technology-Stack/`](02-Technology-Stack/) | ACTIVE | Technology decisions — .NET, Python, databases, infrastructure |
| [`03-Architecture/`](03-Architecture/) | ACTIVE | System architecture, components, data flow, ADRs |
| [`04-Implementation-Phases/`](04-Implementation-Phases/) | ACTIVE | Phase 1–6 implementation plans |
| [`05-DotNet-Capabilities/`](05-DotNet-Capabilities/) | ACTIVE | .NET 10, Semantic Kernel, Aspire, SignalR, Blazor, EF Core notes |
| [`06-Python-Integration/`](06-Python-Integration/) | ACTIVE | Python interop strategies — Python.NET, gRPC, process bridging |
| [`07-Reference/`](07-Reference/) | ACTIVE | Glossary, links, tooling guides |
| [`architecture/opencode-integration-architecture.md`](architecture/opencode-integration-architecture.md) | ACTIVE | Provider integration architecture walkthrough |
| [`full-project-documentation.md`](full-project-documentation.md) | ACTIVE | Consolidated end-to-end project documentation |
| [`PARITY-ROADMAP.md`](PARITY-ROADMAP.md) | ACTIVE | Feature-parity roadmap and phase status |

## Operations

| Document | Freshness | Contents |
|---|---|---|
| [`OPERATIONS.md`](OPERATIONS.md) | ACTIVE | Day-to-day operations runbook |
| [`deployment-operations.md`](deployment-operations.md) | ACTIVE | Host deploy runbook (full-tree archive, verify, hygiene) |
| [`docker-deployment.md`](docker-deployment.md) | ACTIVE | Docker stack: dev mode and full container mode |
| [`CI-CD.md`](CI-CD.md) | ACTIVE | Branch protection and required status checks |
| [`key-management.md`](key-management.md) | ACTIVE | API-key lifecycle and envelope-encryption / master-key management |
| [`gemini-broker-operations.md`](gemini-broker-operations.md) | ACTIVE | Broker-managed provider slots, health and recovery |
| [`portal-routing-config-2026-08-06.md`](portal-routing-config-2026-08-06.md) | ACTIVE | What is configurable from the dashboard vs hardcoded |
| [`../scripts/audit-gateway-concurrency.sh`](../scripts/audit-gateway-concurrency.sh) | ACTIVE | Concurrency/isolation audit helper |

## OAuth and providers

| Document | Freshness | Contents |
|---|---|---|
| [`oauth-integration.md`](oauth-integration.md) | ACTIVE | Provider OAuth integration overview |
| [`google-oauth-login.md`](google-oauth-login.md) | ACTIVE | Google login + Gemini provider OAuth setup |
| [`oauth-token-pool-execution-plan.md`](oauth-token-pool-execution-plan.md) | ACTIVE | Multi-account token pool design and execution notes |
| [`chatgpt-oauth-research-plan.md`](chatgpt-oauth-research-plan.md) | ACTIVE | ChatGPT/Codex OAuth research and account-pool findings |
| [`oauth-redesign-implementation.md`](oauth-redesign-implementation.md) | ACTIVE | OAuth UI/flow redesign — what shipped |
| [`oauth-redesign-plan.md`](oauth-redesign-plan.md) | HISTORICAL | Original redesign plan (superseded by the implementation doc) |
| [`schema-oauth-2026-07.md`](schema-oauth-2026-07.md) | HISTORICAL | OAuth schema snapshot from July 2026 |
| [`fallback-provider-2026-08-06.md`](fallback-provider-2026-08-06.md) | HISTORICAL | Fallback-chain provider work and its live verification |

## Agent behaviour and verification records

| Document | Freshness | Contents |
|---|---|---|
| [`tool-calls-and-ai-gateway.md`](tool-calls-and-ai-gateway.md) | ACTIVE | Tool-calling analysis — native vs gateway-routed |
| [`verification-codex-complex-tool-loop-2026-08-21.md`](verification-codex-complex-tool-loop-2026-08-21.md) | HISTORICAL | Recorded production complex tool-loop verification |
| [`LESSONS_LEARNED.md`](LESSONS_LEARNED.md) | ACTIVE | Living operational lessons, pitfalls and recovery patterns |

## Keeping docs honest

1. `code` and fresh `git` evidence outrank curated plans; plans outrank other docs; chat history is last.
2. Regenerate every number instead of trusting it:
   `dotnet test Arkana.slnx -c Release --no-build --nologo | grep -E "Passed!|Failed!"`.
3. When behaviour changes, update the affected doc in the same commit and relabel anything that
   becomes a point-in-time record as **HISTORICAL**.
