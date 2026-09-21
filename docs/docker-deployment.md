# ARKANA GATEWAY — Docker Deployment Guide
# ===========================================

This guide covers two deployment modes:

- **Development mode** — run the Gateway as a .NET process with Docker infra services
- **Full container mode** — everything runs in Docker via a single `docker compose` command

---

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) with WSL2 backend
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (dev mode only)
- Git

---

## Quick Reference: Ports & Services

| Port | Service | Container |
|------|---------|-----------|
| 5011 | AI Gateway | `arkana-gateway` |
| 5432 | PostgreSQL 16 | `arkana-postgres` |
| 6379 | Redis 7 | `arkana-redis` |
| 5678 | n8n Workflow Engine | `arkana-n8n` |

All services share the `arkana-net` Docker bridge network.

---

## Option A: Development Mode (Recommended for daily work)

This is the existing workflow. Docker runs only the infrastructure (PG, Redis, n8n);
the Gateway runs as a .NET process for fast iteration with hot reload.

### Step 1 — Clone or pull latest

```bash
git checkout main && git pull
```

### Step 2 — Set environment variables

```bash
cp .env.example .env
# Edit .env — set N8N_ENCRYPTION_KEY (openssl rand -hex 32)
```

### Step 3 — Start infrastructure

```bash
docker compose -f deploy/docker-compose.yml up -d postgres redis n8n
```

This starts:
- PostgreSQL 16 on port 5432
- Redis 7 on port 6379
- n8n on port 5678

### Step 4 — Apply database migrations

```bash
dotnet ef database update \
  --project src/Arkana.Infrastructure \
  --startup-project src/Arkana.Gateway.Api
```

### Step 5 — Start the Gateway

```bash
dotnet run --project src/Arkana.Gateway.Api
```

Or with hot reload:

```bash
dotnet watch run --project src/Arkana.Gateway.Api
```

### Step 6 — Set up n8n (first run only)

1. Open http://localhost:5678
2. Create the owner/admin account
3. Note: the Gateway uses cookie-based auth with the same credentials.
   Set `N8N_USER_EMAIL` and `N8N_USER_PASSWORD` in `appsettings.json` or environment.

### Step 7 — Verify

Open http://localhost:5011 — Gateway dashboard.
Open http://localhost:5011/workflows — n8n workflow list.

---

## Option B: Full Container Mode (Production-like)

Everything runs in Docker, including the Gateway itself, via a single command.

### Step 1 — Build the Gateway image

```bash
docker build -t ai-gateway -f Dockerfile .
```

The multi-stage Dockerfile:
1. **`build` stage** — `mcr.microsoft.com/dotnet/sdk:10.0` restores & publishes
2. **`runtime` stage** — `mcr.microsoft.com/dotnet/aspnet:10.0` runs the app

### Step 2 — Set environment

```bash
cp .env.example .env
# Edit .env — fill in all variables
```

Required variables:
- `N8N_ENCRYPTION_KEY` — `openssl rand -hex 32`
- `N8N_USER_EMAIL` — n8n admin email
- `N8N_USER_PASSWORD` — n8n admin password

### Step 3 — Launch everything

```bash
docker compose -f deploy/docker-compose.yml up -d
```

This starts all four services (Gateway included) on the `arkana-net` bridge network.
The Gateway container uses `depends_on` to wait for PG, Redis, and n8n.

### Step 4 — Apply migrations

```bash
docker exec arkana-gateway dotnet ef database update
```

Or if the Gateway's built-in bootstrap handles migration, just wait 30 seconds.

### Step 5 — Verify

```bash
# Check all containers are healthy
docker ps --filter "name=arkana-*"

# Test the Gateway
curl http://localhost:5011/

# Test n8n
curl http://localhost:5678/healthz
```

---

## Rebuilding the Docker Image

### After code changes

```bash
# Rebuild the Gateway image (uses cached layers)
docker build -t ai-gateway -f Dockerfile .

# Restart the Gateway container
docker compose -f deploy/docker-compose.yml up -d gateway
```

### Force a full rebuild (no cache)

```bash
docker build --no-cache -t ai-gateway -f Dockerfile .
docker compose -f deploy/docker-compose.yml up -d gateway
```

### Rebuild just infrastructure (e.g., after changing docker-compose.yml)

```bash
docker compose -f deploy/docker-compose.yml up -d --force-recreate
```

---

## Useful Commands

### Logs

```bash
# Gateway logs
docker logs arkana-gateway -f

# n8n logs
docker logs arkana-n8n -f

# PostgreSQL logs
docker logs arkana-postgres -f
```

### Database

```bash
# Connect to PostgreSQL
docker exec -it arkana-postgres psql -U arkana -d arkana

# List n8n tables
docker exec -it arkana-postgres psql -U arkana -d arkana \
  -c "\dt n8n.*"

# Reset n8n schema (careful — destroys workflows)
docker exec -it arkana-postgres psql -U arkana -d arkana \
  -c "DROP SCHEMA IF EXISTS n8n CASCADE;"
```

### Cleanup

```bash
# Stop everything, remove containers
docker compose -f deploy/docker-compose.yml down

# Stop everything AND delete volumes (loses all data)
docker compose -f deploy/docker-compose.yml down -v

# Remove the Gateway image
docker rmi ai-gateway
```

---

## Configuration via Environment Variables

The Gateway uses .NET's configuration hierarchy:

1. `appsettings.json`
2. `appsettings.Development.json`
3. Environment variables (Docker or shell)
4. Command-line args

In Docker, set values via the `environment` block in `docker-compose.yml`.
Dot-separated config keys use double underscores in env vars:

| appsettings.json path | Docker env var |
|-----------------------|----------------|
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` |
| `N8n:BaseUrl` | `N8n__BaseUrl` |
| `N8n:UserEmail` | `N8n__UserEmail` |
| `N8n:UserPassword` | `N8n__UserPassword` |

---

## Troubleshooting

### Gateway can't connect to PostgreSQL

```bash
# Shell into the Gateway container and test
docker exec -it arkana-gateway bash
curl postgres:5432  # should get "empty reply from server"
```

### n8n workflow page shows error

1. Confirm n8n is running: `curl http://localhost:5678/healthz`
2. Confirm n8n user credentials are correct in `N8n__UserEmail` / `N8n__UserPassword`
3. Check Gateway logs for authentication details

### Volumes and persistent data

| Service | Volume | Path inside container |
|---------|--------|----------------------|
| PostgreSQL | `pgdata` | `/var/lib/postgresql/data` |
| Redis | `redisdata` | `/data` |
| n8n | `n8ndata` | `/home/node/.n8n` |

To back up: `docker run --rm -v pgdata:/data -v $(pwd):/backup alpine tar czf /backup/pgdata.tar.gz -C /data .`
