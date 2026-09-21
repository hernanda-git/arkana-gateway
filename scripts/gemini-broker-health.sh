#!/usr/bin/env bash
set -euo pipefail

compose_file="${COMPOSE_FILE:-deploy/docker-compose.gemini-broker.yml}"
project="${COMPOSE_PROJECT_NAME:-arkana-dev}"
services=(gemini-broker-a gemini-broker-b)

printf 'PASS: compose file readable: %s\n' "$compose_file"
docker compose -p "$project" -f "$compose_file" config >/dev/null
for service in "${services[@]}"; do
  container="${project}-${service}-1"
  status="$(docker inspect -f '{{.State.Health.Status}}' "$container" 2>/dev/null || true)"
  case "$status" in
    healthy) printf 'PASS: %s healthy\n' "$service" ;;
    *) printf 'BLOCKED: %s health=%s\n' "$service" "${status:-missing}" >&2; exit 1 ;;
  esac
done
