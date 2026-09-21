#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="${GATEWAY_DEPLOY_DIR:-$HOME/arkana-deploy}"
SERVICE="${GATEWAY_SERVICE:-gateway}"
CONTAINER="${GATEWAY_CONTAINER:-arkana-gateway}"
BACKUP_DIR="${GATEWAY_BACKUP_DIR:-$DEPLOY_DIR/backups}"
STAMP="${GATEWAY_BACKUP_STAMP:-$(date -u '+%Y%m%dT%H%M%SZ')}"
MANIFEST="$BACKUP_DIR/gateway-$STAMP.env"

fail(){ printf 'BACKUP_FAIL: %s\n' "$*" >&2; exit 1; }
command -v docker >/dev/null || fail "docker is required"
mkdir -p "$BACKUP_DIR"
live_image="$(docker inspect "$CONTAINER" --format '{{.Image}}' 2>/dev/null)" || fail "container not running: $CONTAINER"
[ -n "$live_image" ] || fail "empty live image id"
rollback_tag="${GATEWAY_ROLLBACK_TAG:-arkana-dev-gateway:rollback-before-$STAMP}"
docker tag "$live_image" "$rollback_tag"
rollback_id="$(docker image inspect "$rollback_tag" --format '{{.Id}}')"
[ "$live_image" = "$rollback_id" ] || fail "rollback tag does not match live image"
compose_project="$(docker inspect "$CONTAINER" --format '{{ index .Config.Labels "com.docker.compose.project" }}')"
compose_service="$(docker inspect "$CONTAINER" --format '{{ index .Config.Labels "com.docker.compose.service" }}')"
{
  printf 'created_utc=%s\n' "$STAMP"
  printf 'container=%s\n' "$CONTAINER"
  printf 'service=%s\n' "$SERVICE"
  printf 'live_image_id=%s\n' "$live_image"
  printf 'rollback_tag=%s\n' "$rollback_tag"
  printf 'rollback_image_id=%s\n' "$rollback_id"
  printf 'compose_project=%s\n' "$compose_project"
  printf 'compose_service=%s\n' "$compose_service"
  printf 'source_sha=%s\n' "${GATEWAY_SOURCE_SHA:-unknown}"
} > "$MANIFEST"
printf 'BACKUP_PASS: manifest=%s\n' "$MANIFEST"
printf 'BACKUP_LIVE_IMAGE=%s\nBACKUP_ROLLBACK_TAG=%s\n' "$live_image" "$rollback_tag"
