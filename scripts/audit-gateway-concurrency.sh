#!/usr/bin/env bash
set -euo pipefail

ROOT="${GATEWAY_REPO_ROOT:-C:/Workspace/gateway}"
COORD_DIR="${GATEWAY_COORD_DIR:-$(dirname "$ROOT")/.gateway-coordination}"
CLAIM_DIR="$COORD_DIR/claims"
LEASE_DIR="${GATEWAY_LEASE_DIR:-$HOME/arkana-deploy/.gateway-deploy-lease}"
py() { if python3 --version >/dev/null 2>&1; then python3 "$@"; elif python --version >/dev/null 2>&1; then python "$@"; elif command -v uv >/dev/null; then uv run python "$@"; else printf 'AUDIT_FAIL: working python3, python, or uv is required\n' >&2; exit 1; fi; }

[ -d "$ROOT/.git" ] || { printf 'AUDIT_FAIL: repository not found: %s\n' "$ROOT" >&2; exit 1; }
printf '%s\n' '=== WORKTREES ==='
git -C "$ROOT" worktree list --porcelain
printf '%s\n' '=== BRANCHES ==='
git -C "$ROOT" branch -avv
printf '%s\n' '=== ACTIVE CLAIMS ==='
if [ -d "$CLAIM_DIR" ]; then
  for claim in "$CLAIM_DIR"/*.json; do
    [ -f "$claim" ] || continue
    printf '%s\n' "--- $claim"
    py - "$claim" <<'PY'
import json, sys
from datetime import datetime, timezone
p=json.load(open(sys.argv[1]))
expiry=p.get('expires_utc','')
try: active=datetime.fromisoformat(expiry.replace('Z','+00:00')) > datetime.now(timezone.utc)
except Exception: active=True
print(json.dumps({'lane':p.get('lane'),'owner':p.get('owner'),'worktree':p.get('worktree'),'files':p.get('files',[]),'expires_utc':expiry,'active':active}, separators=(',',':')))
PY
  done
else
  printf '%s\n' 'none'
fi
printf '%s\n' '=== DEPLOY LEASE ==='
if [ -f "$LEASE_DIR/metadata" ]; then cat "$LEASE_DIR/metadata"; else printf '%s\n' 'LEASE_NOT_HELD'; fi
printf '%s\n' '=== PROTECTED CHECKOUT ==='
git -C "$ROOT" status --short --branch
