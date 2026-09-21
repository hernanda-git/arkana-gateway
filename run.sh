#!/usr/bin/env bash
# ── ARKANA GATEWAY — Rebuild & Redeploy ──
# Stops running containers, rebuilds images (including Blazor app),
# and starts the full stack.

set -euo pipefail

echo ""
echo "=== ARKANA GATEWAY — Rebuild & Redeploy ==="

# Step 1: Tear down existing containers
echo ""
echo "[1/3] Stopping running containers..."
docker compose -f deploy/docker-compose.yml down

# Step 2: Rebuild & start (--build forces Docker to rebuild the gateway image)
echo ""
echo "[2/3] Rebuilding images and starting services..."
docker compose -f deploy/docker-compose.yml up -d --build

# Step 3: Show status
echo ""
echo "[3/3] Container status:"
docker compose -f deploy/docker-compose.yml ps

echo ""
echo "=== Done! Gateway should be available at http://localhost:5011 ==="
echo "Blazor dashboard: http://localhost:5011/dashboard"
