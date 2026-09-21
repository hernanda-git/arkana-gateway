#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || {
  printf 'PREFLIGHT_FAIL: not inside a git worktree\n' >&2
  exit 1
}
BRANCH="$(git -C "$ROOT" symbolic-ref --short HEAD 2>/dev/null || true)"
WORKTREE="$(git -C "$ROOT" rev-parse --show-toplevel)"
COORD_DIR="${GATEWAY_COORD_DIR:-$(dirname "$ROOT")/.gateway-coordination}"

fail() { printf 'PREFLIGHT_FAIL: %s\n' "$*" >&2; exit 1; }
pass() { printf 'PREFLIGHT_PASS: %s\n' "$*"; }

[ "$WORKTREE" != "C:/Workspace/arkana-gateway" ] && [ "$WORKTREE" != "/c/Workspace/gateway" ] || fail "protected checkout is not a work lane"
[[ "$BRANCH" =~ ^(feat|fix|docs|chore|refactor|audit|reconcile)/.+-[0-9]{8}(-[0-9]+)?$ ]] || fail "branch is not dated/conventional: ${BRANCH:-detached}"
git diff --check
mkdir -p "$COORD_DIR/claims" "$COORD_DIR/locks"
git fetch origin --prune >/dev/null
base="$(git rev-parse origin/main)"
head="$(git rev-parse HEAD)"
git merge-base --is-ancestor "$base" HEAD || fail "lane is behind origin/main; rebase before editing"
pass "lane=$BRANCH worktree=$WORKTREE"
pass "origin/main=$base head=$head"
printf 'PREFLIGHT_COORD_DIR=%s\n' "$COORD_DIR"
