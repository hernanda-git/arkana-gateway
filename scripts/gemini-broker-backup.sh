#!/usr/bin/env bash
set -euo pipefail
umask 077

compose_file="${COMPOSE_FILE:-deploy/docker-compose.gemini-broker.yml}"
project="${COMPOSE_PROJECT_NAME:-arkana-dev}"
out_dir="${BACKUP_DIR:-./backups/gemini-broker}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$out_dir"

for slot in a b; do
  volume="arkana-gemini-slot-${slot}-auth"
  archive="$out_dir/gemini-slot-${slot}-auth-${timestamp}.tar.gz"
  docker volume inspect "$volume" >/dev/null
  docker run --rm -v "$volume:/src:ro" -v "$(cd "$out_dir" && pwd):/out" alpine:3.20 tar -czf "/out/$(basename "$archive")" -C /src . >/dev/null
  chmod 600 "$archive"
  sha256sum "$archive" | cut -d' ' -f1 > "$archive.sha256"
  chmod 600 "$archive.sha256"
  printf 'PASS: slot-%s backup created; sha256 recorded in %s\n' "$slot" "$archive.sha256"
done
# Deliberately do not invoke compose or print archive contents/secrets.
: "$compose_file" "$project"
