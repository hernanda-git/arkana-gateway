#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="${GATEWAY_DEPLOY_DIR:-$HOME/arkana-deploy}"
COMPOSE_FILE="${GATEWAY_COMPOSE_FILE:-$DEPLOY_DIR/docker-compose.yml}"
CONTAINER="${GATEWAY_CONTAINER:-arkana-gateway}"
CANDIDATE_TAG="${GATEWAY_CANDIDATE_TAG:?set GATEWAY_CANDIDATE_TAG to an immutable candidate image}"
LEASE_SCRIPT="${GATEWAY_LEASE_SCRIPT:-$ROOT/scripts/deploy-lease.sh}"
BACKUP_SCRIPT="${GATEWAY_BACKUP_SCRIPT:-$ROOT/scripts/backup-gateway-image.sh}"
HEALTH_TIMEOUT="${GATEWAY_HEALTH_TIMEOUT:-180}"

fail(){ printf 'DEPLOY_FAIL: %s\n' "$*" >&2; exit 1; }
[ -f "$COMPOSE_FILE" ] || fail "compose file missing: $COMPOSE_FILE"
docker image inspect "$CANDIDATE_TAG" >/dev/null || fail "candidate image missing: $CANDIDATE_TAG"
command -v curl >/dev/null || fail "curl is required for readiness checks"

rollback_tag=""
rollback() {
  [ -n "$rollback_tag" ] || return 0
  printf 'DEPLOY_ROLLBACK: restoring %s\n' "$rollback_tag" >&2
  docker tag "$rollback_tag" arkana-dev-gateway:latest || true
  docker compose -f "$COMPOSE_FILE" up -d --no-build --no-deps "$GATEWAY_SERVICE" >/dev/null || true
}

run_deploy() {
  export GATEWAY_SOURCE_SHA="${GATEWAY_SOURCE_SHA:-unknown}"
  backup_output="$("$BACKUP_SCRIPT")"
  printf '%s\n' "$backup_output"
  rollback_tag="$(printf '%s\n' "$backup_output" | sed -n 's/^BACKUP_ROLLBACK_TAG=//p')"
  [ -n "$rollback_tag" ] || fail "backup did not return rollback tag"
  trap 'rc=$?; if [ "$rc" -ne 0 ]; then rollback; fi; exit "$rc"' EXIT
  candidate_id="$(docker image inspect "$CANDIDATE_TAG" --format '{{.Id}}')"
  docker tag "$CANDIDATE_TAG" arkana-dev-gateway:latest
  docker compose -f "$COMPOSE_FILE" up -d --no-build --no-deps "$GATEWAY_SERVICE" >/dev/null
  deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
  while :; do
    health="$(docker inspect "$CONTAINER" --format '{{.State.Health.Status}}' 2>/dev/null || true)"
    printf 'DEPLOY_HEALTH=%s\n' "$health"
    [ "$health" = healthy ] && break
    [ "$(date +%s)" -lt "$deadline" ] || {
      printf 'DEPLOY_ROLLBACK: candidate readiness timeout\n' >&2
      docker tag "$rollback_tag" arkana-dev-gateway:latest
      docker compose -f "$COMPOSE_FILE" up -d --no-build --no-deps "$GATEWAY_SERVICE" >/dev/null || true
      fail "candidate did not become healthy"
    }
    sleep 5
  done
  curl_flags=(-sS -m 15)
  [ "${GATEWAY_INSECURE_TLS:-0}" = 1 ] && curl_flags+=(-k)
  code="$(curl "${curl_flags[@]}" -o /dev/null -w '%{http_code}' "${GATEWAY_URL:-http://127.0.0.1:5011}/health")"
  [ "$code" = 200 ] || fail "/health returned HTTP $code"
  running_id="$(docker inspect "$CONTAINER" --format '{{.Image}}')"
  [ "$running_id" = "$candidate_id" ] || fail "running image $running_id differs from candidate $candidate_id"
  printf 'DEPLOY_PASS: service=%s image=%s health=%s health_http=%s\n' "$GATEWAY_SERVICE" "$running_id" "$health" "$code"
  trap - EXIT
}

export GATEWAY_SERVICE="${GATEWAY_SERVICE:-gateway}"
if [ "${GATEWAY_DEPLOY_LEASE_HELD:-0}" = 1 ]; then
  run_deploy
else
  export GATEWAY_DEPLOY_LEASE_HELD=1
  export GATEWAY_LANE="${GATEWAY_LANE:-manual-deploy}"
  export GATEWAY_CURRENT_IMAGE_DIGEST="${GATEWAY_CURRENT_IMAGE_DIGEST:-unknown}"
  "$LEASE_SCRIPT" acquire -- "$0" --leased
fi
