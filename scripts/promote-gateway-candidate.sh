#!/usr/bin/env bash
set -euo pipefail
cd "$HOME/arkana-deploy"
CANDIDATE_TAG="${CANDIDATE_TAG:-arkana-dev-gateway:release-reconcile-20260901}"
ROLLBACK_TAG="${ROLLBACK_TAG:-arkana-dev-gateway:rollback-before-release-reconcile-20260901T020655Z}"
APPROVAL_DIR="${APPROVAL_DIR:-$HOME/arkana-deploy/release-approvals}"
APPROVAL_FILE="${APPROVAL_FILE:-$APPROVAL_DIR/${CANDIDATE_TAG##*:}.approved}"
FAILURE=0

mkdir -p "$APPROVAL_DIR"
test -f "$APPROVAL_FILE" || { echo "PROMOTION_BLOCKED: missing approval marker $APPROVAL_FILE" >&2; exit 1; }
approved_digest="$(tr -d '[:space:]' < "$APPROVAL_FILE")"
candidate_digest="$(docker image inspect -f '{{.Id}}' "$CANDIDATE_TAG")"
test "$approved_digest" = "$candidate_digest" || {
  echo "PROMOTION_BLOCKED: approval digest does not match candidate" >&2
  exit 1
}

rollback() {
  echo "PROMOTION_FAILED: rolling back gateway only"
  docker tag "$ROLLBACK_TAG" arkana-dev-gateway:latest
  docker compose up -d --no-build --no-deps gateway >/dev/null
  for _ in $(seq 1 12); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' arkana-gateway 2>/dev/null || true)" = healthy ] && break
    sleep 5
  done
  echo "ROLLBACK_IMAGE=$(docker inspect -f '{{.Image}}' arkana-gateway)"
  echo "ROLLBACK_HEALTH=$(docker inspect -f '{{.State.Health.Status}}' arkana-gateway)"
}
trap 'rc=$?; if [ "$rc" -ne 0 ]; then rollback; fi; exit "$rc"' EXIT

docker tag "$CANDIDATE_TAG" arkana-dev-gateway:latest
docker compose up -d --no-build --no-deps gateway >/dev/null
for _ in $(seq 1 18); do
  health=$(docker inspect -f '{{.State.Health.Status}}' arkana-gateway 2>/dev/null || true)
  echo "health=$health"
  [ "$health" = healthy ] && break
  sleep 5
done
[ "$(docker inspect -f '{{.State.Health.Status}}' arkana-gateway)" = healthy ]

set -a
. "$HOME/arkana-deploy/.env"
set +a
jar="/tmp/gw-release-login-$$.jar"
login_html="/tmp/gw-release-login-$$.html"
response="/tmp/gw-release-login-$$.response"
trap 'rm -f "$jar" "$login_html" "$response"' INT TERM
curl -skm 15 -c "$jar" http://127.0.0.1:5011/login -o "$login_html"
token=$(grep -oP 'name="__RequestVerificationToken"[^>]*value="\K[^"]+' "$login_html" | head -1)
test -n "$token"
post=$(curl -skm 15 -b "$jar" -c "$jar" -o "$response" -w '%{http_code}' -X POST http://127.0.0.1:5011/login --data-urlencode "__RequestVerificationToken=$token" --data-urlencode "username=$ARKANA_ADMIN_USER" --data-urlencode "password=$ARKANA_ADMIN_PASSWORD")
test "$post" = 302
# Do not print the cookie value; only assert that the cookie exists.
grep -qE '(\.AspNetCore\.Cookies|\.Arkana\.Auth\.v2)' "$jar"
auth_root=$(curl -skm 15 -b "$jar" -o "$response" -w '%{http_code}' http://127.0.0.1:5011/)
test "$auth_root" = 200
grep -qiE 'AI Gateway|Dashboard|Logout' "$response"
nego=$(curl -skm 15 -b "$jar" -o "$response" -w '%{http_code}' -X POST 'http://127.0.0.1:5011/_blazor/negotiate?negotiateVersion=1' -H 'Content-Length: 0')
test "$nego" = 200
rm -f "$jar" "$login_html" "$response"

echo "PROMOTION_PASS"
echo "IMAGE=$(docker inspect -f '{{.Image}}' arkana-gateway)"
echo "HEALTH=$(docker inspect -f '{{.State.Health.Status}}' arkana-gateway)"
trap - EXIT
