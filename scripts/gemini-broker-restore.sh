#!/usr/bin/env bash
set -euo pipefail
umask 077

archive="${BACKUP_ARCHIVE:?Set BACKUP_ARCHIVE to one slot auth archive}"
slot="${SLOT:?Set SLOT to a or b}"
case "$slot" in a|b) ;; *) printf 'BLOCKED: SLOT must be a or b\n' >&2; exit 1 ;; esac
[ -f "$archive" ] || { printf 'BLOCKED: backup archive missing\n' >&2; exit 1; }
[ -f "$archive.sha256" ] || { printf 'BLOCKED: checksum file missing\n' >&2; exit 1; }
expected="$(tr -d '[:space:]' < "$archive.sha256")"
actual="$(sha256sum "$archive" | cut -d' ' -f1)"
[ "$expected" = "$actual" ] || { printf 'BLOCKED: checksum mismatch\n' >&2; exit 1; }
volume="arkana-gemini-slot-${slot}-auth"
docker volume inspect "$volume" >/dev/null
# Restore is explicit and refuses to touch a running slot; no compose action is done.
container="${COMPOSE_PROJECT_NAME:-arkana-dev}-gemini-broker-${slot}-1"
state="$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || printf false)"
[ "$state" = false ] || { printf 'BLOCKED: stop slot-%s before restore\n' "$slot" >&2; exit 1; }
docker run --rm -v "$volume:/dst" -v "$(cd "$(dirname "$archive")" && pwd):/in:ro" alpine:3.20 sh -c 'find /dst -mindepth 1 -exec rm -rf {} + && tar -xzf "/in/$(basename "$1")" -C /dst' sh "$(basename "$archive")" >/dev/null
printf 'PASS: slot-%s auth volume restored; archive checksum verified\n' "$slot"
