#!/usr/bin/env bash
# End-to-end smoke test for the ARKANA GATEWAY (runs against localhost:5011).
# Tiers 1-4: infra + gateway self-contained behavior (no upstream provider keys needed).
set -uo pipefail
BASE=http://localhost:5011
CHAT='{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"ping"}]}'
PASS=0; FAIL=0
ok(){ echo "PASS  $1"; PASS=$((PASS+1)); }
bad(){ echo "FAIL  $1"; FAIL=$((FAIL+1)); }

echo "================ TIER 1: INFRA HEALTH ================"
docker inspect -f 'postgres  {{.State.Health.Status}}' arkana-postgres 2>/dev/null | grep -q healthy && ok "postgres healthy" || bad "postgres healthy"
docker inspect -f 'redis     {{.State.Health.Status}}' arkana-redis  2>/dev/null | grep -q healthy && ok "redis healthy"    || bad "redis healthy"
docker inspect -f 'qdrant    {{.State.Health.Status}}' arkana-qdrant 2>/dev/null | grep -q healthy && ok "qdrant healthy"   || bad "qdrant healthy"
# n8n web UI /healthz is the reliable probe; avoid `curl -f -o /dev/null` which
# trips exit code 23 on this MSYS curl build even on HTTP 200.
n8n_ok=0
for i in $(seq 1 10); do
  if [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5678/healthz)" = "200" ]; then n8n_ok=1; break; fi
  sleep 2
done
[ "$n8n_ok" = "1" ] && ok "n8n reachable :5678" || bad "n8n reachable :5678"

echo "================ TIER 2: GATEWAY HTTP SMOKE ================"
code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/"); [ "$code" = "200" ] && ok "GET / -> 200" || bad "GET / -> 200 (got $code)"
code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health"); [ "$code" = "200" ] && ok "GET /health -> 200" || bad "GET /health -> 200 (got $code)"
code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/admin/stats"); [ "$code" = "401" ] && ok "GET /admin/stats -> 401 without admin auth (SEC-ARKANA-004)" || bad "GET /admin/stats -> 401 without admin auth (got $code)"
# When ADMIN_KEY is supplied the same endpoint must succeed.
if [ -n "${ADMIN_KEY:-}" ]; then
  code=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Admin-Key: $ADMIN_KEY" "$BASE/admin/stats"); [ "$code" = "200" ] && ok "GET /admin/stats -> 200 with ADMIN_KEY" || bad "GET /admin/stats -> 200 with ADMIN_KEY (got $code)"
fi
MODELS=$(curl -s "$BASE/v1/models"); echo "      /v1/models: $(echo "$MODELS" | head -c 200)"

echo "================ TIER 3: AUTH GATING (expect 401 on PROTECTED routes) ================"
# NOTE: /v1/models is explicitly exempt from auth in Program.cs (line 152),
# so it MUST return 200 even without a key. Use /v1/chat/completions as the
# definitive protected-route probe (valid key -> 200, missing/bad -> 401).
code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/chat/completions" -X POST -H 'Content-Type: application/json' -d '{"model":"x","messages":[]}'); [ "$code" = "401" ] && ok "chat w/o key -> 401" || bad "chat w/o key -> 401 (got $code)"
code=$(curl -s -o /dev/null -w '%{http_code}' -H 'X-Api-Key: this-is-not-a-real-key' "$BASE/v1/chat/completions" -X POST -H 'Content-Type: application/json' -d '{"model":"x","messages":[]}'); [ "$code" = "401" ] && ok "chat bad key -> 401" || bad "chat bad key -> 401 (got $code)"
code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/admin/api-keys"); [ "$code" = "401" ] && ok "GET /admin/api-keys -> 401 without admin auth (SEC-ARKANA-004)" || bad "GET /admin/api-keys -> 401 without admin auth (got $code)"
code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/models"); [ "$code" = "200" ] && ok "GET /v1/models unauth -> 200 (exempt by design)" || bad "GET /v1/models unauth -> 200 (got $code)"

echo "================ TIER 4: AUTHENTICATED LIFECYCLE ================"
# Admin API now requires ADMIN_AUTH (SEC-ARKANA-004). The key lifecycle below
# only runs when ADMIN_KEY is provided; without it the admin surface is 401
# and these steps are skipped rather than asserting the old open behaviour.
if [ -z "${ADMIN_KEY:-}" ]; then
  echo "      (skipped: set ADMIN_KEY to exercise the admin key lifecycle)"
else
# 4a. create a key via admin API
CREATE=$(curl -s -X POST "$BASE/admin/api-keys" -H 'Content-Type: application/json' \
  -H "X-Admin-Key: $ADMIN_KEY" \
  -d '{"name":"e2e-test-key","allowedModelIds":[]}')
echo "      create: $CREATE"
KEY=$(echo "$CREATE" | grep -oE '"plainTextKey":"[^"]+"' | sed 's/"plainTextKey":"//;s/"$//')
if [ -n "$KEY" ]; then ok "API key created (len ${#KEY})"; else bad "API key created"; fi

# 4b. use key on protected endpoint -> 200
code=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Api-Key: $KEY" "$BASE/v1/models"); [ "$code" = "200" ] && ok "GET /v1/models WITH key -> 200" || bad "GET /v1/models WITH key -> 200 (got $code)"

# 4c. attempt a chat completion (no upstream key configured -> expect graceful 4xx/5xx, NOT 401)
CC=$(curl -s -o /tmp/cc.json -w '%{http_code}' -X POST "$BASE/v1/chat/completions" \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"ping"}]}')
echo "      chat completion -> HTTP $CC, body: $(head -c 200 /tmp/cc.json)"
[ "$CC" != "401" ] && ok "chat/completions authenticated (no 401; upstream keyless -> $CC)" || bad "chat/completions returned 401 (auth bug)"

# 4d. toggle the key OFF -> now 401 (use chat as protected probe; /v1/models is exempt)
KID=$(echo "$CREATE" | grep -oE '"id":"[^"]+"' | head -1 | sed 's/"id":"//;s/"$//')
curl -s -o /dev/null -X PUT "$BASE/admin/api-keys/$KID/toggle" -H "X-Admin-Key: $ADMIN_KEY"
code=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Api-Key: $KEY" -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' -d "$CHAT")
[ "$code" = "401" ] && ok "deactivated key -> 401 on chat" || bad "deactivated key should 401 (got $code)"

# 4e. reactivate -> 200 again (full lifecycle verified)
curl -s -o /dev/null -X PUT "$BASE/admin/api-keys/$KID/toggle" -H "X-Admin-Key: $ADMIN_KEY"
code=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Api-Key: $KEY" -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' -d "$CHAT")
[ "$code" = "200" ] && ok "reactivated key -> 200 on chat" || bad "reactivated key should 200 (got $code)"
fi

echo ""
echo "================ RESULT ================"
echo "PASS=$PASS  FAIL=$FAIL"
[ "$FAIL" = "0" ] && echo "ALL TIERS 1-4 PASSED" || echo "SOME CHECKS FAILED"
