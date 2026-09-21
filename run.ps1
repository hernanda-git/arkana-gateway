# ── ARKANA GATEWAY — Rebuild & Redeploy ──
# Stops running containers, rebuilds images (including Blazor app),
# and starts the full stack.

Write-Host "`n=== ARKANA GATEWAY — Rebuild & Redeploy ===" -ForegroundColor Cyan

# Step 1: Tear down existing containers
Write-Host "`n[1/3] Stopping running containers..." -ForegroundColor Yellow
docker compose -f deploy/docker-compose.yml down

# Step 2: Rebuild & start (--build forces Docker to rebuild the gateway image)
Write-Host "`n[2/3] Rebuilding images and starting services..." -ForegroundColor Yellow
docker compose -f deploy/docker-compose.yml up -d --build

# Step 3: Show status
Write-Host "`n[3/3] Container status:" -ForegroundColor Yellow
docker compose -f deploy/docker-compose.yml ps

Write-Host "`n=== Done! Gateway should be available at http://localhost:5011 ===" -ForegroundColor Green
Write-Host "Blazor dashboard: http://localhost:5011/dashboard" -ForegroundColor Green
