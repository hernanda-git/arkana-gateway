#!/usr/bin/env bash
set -euo pipefail

ACTION="${1:-status}"
LEASE_DIR="${GATEWAY_LEASE_DIR:-$HOME/arkana-deploy/.gateway-deploy-lease}"
LEASE_TTL_SECONDS="${GATEWAY_LEASE_TTL_SECONDS:-1800}"
LANE="${GATEWAY_LANE:-unknown}"
OWNER="${GATEWAY_OWNER:-${USER:-unknown}@$(hostname)}"

fail(){ printf 'LEASE_FAIL: %s\n' "$*" >&2; exit 1; }
now(){ date -u '+%Y-%m-%dT%H:%M:%SZ'; }
case "$ACTION" in
  acquire)
    [ "$#" -ge 3 ] && [ "$2" = "--" ] || fail "acquire syntax: acquire -- command [args...]"
    if [ -d "$LEASE_DIR" ]; then
      expires="$(sed -n 's/^expires_epoch=//p' "$LEASE_DIR/metadata" 2>/dev/null || true)"
      if [[ "$expires" =~ ^[0-9]+$ ]] && [ "$expires" -lt "$(date +%s)" ]; then
        stale="$LEASE_DIR.expired.$$"
        mv "$LEASE_DIR" "$stale" 2>/dev/null || fail "lease changed while expiring: $LEASE_DIR"
        rm -rf "$stale"
      else
        fail "lease already held at $LEASE_DIR"
      fi
    fi
    mkdir "$LEASE_DIR" 2>/dev/null || fail "lease already held at $LEASE_DIR"
    trap 'rm -rf "$LEASE_DIR"' EXIT INT TERM
    start_epoch="$(date +%s)"
    expires_epoch=$((start_epoch + LEASE_TTL_SECONDS))
    {
      printf 'lane=%s\n' "$LANE"
      printf 'owner=%s\n' "$OWNER"
      printf 'host=%s\n' "$(hostname)"
      printf 'pid=%s\n' "$$"
      printf 'started_utc=%s\n' "$(now)"
      printf 'expires_epoch=%s\n' "$expires_epoch"
      printf 'image_digest=%s\n' "${GATEWAY_CURRENT_IMAGE_DIGEST:-unknown}"
    } > "$LEASE_DIR/metadata"
    printf 'LEASE_ACQUIRED: %s\n' "$LEASE_DIR"
    shift 2
    "$@"
    ;;
  release)
    [ -d "$LEASE_DIR" ] || { printf 'LEASE_NOT_HELD\n'; exit 0; }
    grep -Fqx "lane=$LANE" "$LEASE_DIR/metadata" 2>/dev/null || fail "lease lane mismatch"
    grep -Fqx "owner=$OWNER" "$LEASE_DIR/metadata" 2>/dev/null || fail "lease owner mismatch"
    grep -Fqx "host=$(hostname)" "$LEASE_DIR/metadata" 2>/dev/null || fail "lease host mismatch"
    rm -rf "$LEASE_DIR"
    printf 'LEASE_RELEASED: %s\n' "$LEASE_DIR"
    ;;
  status)
    if [ -f "$LEASE_DIR/metadata" ]; then cat "$LEASE_DIR/metadata"; else printf 'LEASE_NOT_HELD\n'; fi
    ;;
  *) fail "usage: deploy-lease.sh {acquire|release|status} [-- command]";;
esac
