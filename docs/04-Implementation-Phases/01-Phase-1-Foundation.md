# Phase 1 — Foundation

## Goal
Build the core AI Gateway with authentication, basic routing, and credential management.

## Tasks

### 1.1 Solution Setup
- [ ] Create .NET 10 solution with `Arkana.Gateway.Api` project
- [ ] Configure Aspire AppHost for local development
- [ ] Set up PostgreSQL + Redis in Docker Compose
- [ ] Configure OpenTelemetry + Aspire Dashboard

### 1.2 Core Gateway API
- [ ] Implement POST `/v1/chat/completions` endpoint (OpenAI-compatible)
- [ ] Implement API Key authentication middleware
- [ ] Implement JWT Bearer authentication
- [ ] Add request/response logging middleware

### 1.3 AI Provider Integration
- [ ] OpenCode connector (via custom HttpClient)
- [ ] OpenAI connector (via Microsoft.Extensions.AI)
- [ ] Gemini connector
- [ ] Anthropic Claude connector
- [ ] Ollama local connector

### 1.4 Token Tracking
- [ ] Implement token counter middleware
- [ ] Store usage data in PostgreSQL
- [ ] Expose usage query endpoints

### 1.5 Credential Management
- [ ] Vault for API keys (encrypted at rest)
- [ ] Key rotation support
- [ ] Per-provider credential configuration

## Success Criteria
- [ ] Gateway responds to chat completion requests via all **5 providers**
- [ ] API key authentication works
- [ ] Token usage recorded in database
- [ ] Credentials stored securely
