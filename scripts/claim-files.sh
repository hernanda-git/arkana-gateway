#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
COORD_DIR="${GATEWAY_COORD_DIR:-$(dirname "$ROOT")/.gateway-coordination}"
CLAIM_DIR="$COORD_DIR/claims"
LANE="${GATEWAY_LANE:-$(git -C "$ROOT" symbolic-ref --short HEAD)}"
OWNER="${GATEWAY_OWNER:-${USER:-unknown}@$(hostname)}"
EXPIRY="${GATEWAY_CLAIM_EXPIRY_UTC:-$(date -u -d '+8 hours' '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u '+%Y-%m-%dT%H:%M:%SZ')}"

fail() { printf 'CLAIM_FAIL: %s\n' "$*" >&2; exit 1; }
[ "$#" -gt 0 ] || fail "usage: claim-files.sh path [path ...]"
mkdir -p "$CLAIM_DIR"
claim="$CLAIM_DIR/${LANE//\//_}.json"
lock="$COORD_DIR/.claims.lock"
while ! mkdir "$lock" 2>/dev/null; do sleep 1; done
trap 'rmdir "$lock" 2>/dev/null || true' EXIT

py() { if python3 --version >/dev/null 2>&1; then python3 "$@"; elif python --version >/dev/null 2>&1; then python "$@"; elif command -v uv >/dev/null; then uv run python "$@"; else fail "working python3, python, or uv is required"; fi; }
py - "$CLAIM_DIR" "$claim" "$LANE" "$OWNER" "$EXPIRY" "$ROOT" "$@" <<'PY'
import json, pathlib, sys
from datetime import datetime, timezone
claim_dir, claim_path, lane, owner, expiry, root, *files = sys.argv[1:]
root_norm = root.replace('\\','/').rstrip('/').casefold()
def normal(p):
    value=p.replace('\\','/').strip()
    lower=value.casefold()
    if lower == root_norm: value=''
    elif lower.startswith(root_norm + '/'): value=value[len(root_norm)+1:]
    elif len(value) >= 2 and value[1] == ':': value=value.split(':',1)[1]
    parts=[part for part in value.split('/') if part not in ('','.')]
    return tuple(part.casefold() for part in parts)
files = sorted(set(normal(p) for p in files))
def overlaps(left, right):
    return left == right or left[:len(right)] == right or right[:len(left)] == left
for path in pathlib.Path(claim_dir).glob('*.json'):
    if str(path) == claim_path: continue
    try: other=json.loads(path.read_text())
    except Exception: continue
    other_files=[normal(p) for p in other.get('files', [])]
    overlap=[left for left in files for right in other_files if overlaps(left, right)]
    try: active=datetime.fromisoformat(other.get('expires_utc','').replace('Z','+00:00')) > datetime.now(timezone.utc)
    except Exception: active=True
    if overlap and active:
        display=', '.join('/'.join(item) for item in overlap)
        print(f"CLAIM_FAIL: overlap with {other.get('lane','unknown')}: {display}", file=sys.stderr)
        raise SystemExit(1)
data={'lane':lane,'worktree':root,'owner':owner,'created_utc':__import__('datetime').datetime.now(__import__('datetime').timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),'files':['/'.join(item) for item in files],'mode':'edit','expires_utc':expiry}
pathlib.Path(claim_path).write_text(json.dumps(data, indent=2)+'\n')
print(f"CLAIM_PASS: {claim_path}")
print('CLAIM_FILES: '+', '.join('/'.join(item) for item in files))
PY
