#!/usr/bin/env bash
# setup-host.sh — apply tracked host OS config (nginx + sslh) idempotently.
#
# Run on the Ubuntu gateway server (gateway-host) as root, from the repo root
# (or with INFRA_DIR pointing at this folder). It:
#   1. Copies deploy/infra/nginx/arkana-gateway.conf -> /etc/nginx/sites-enabled/
#      (the live enabled site; overwrites in place).
#   2. Validates with `nginx -t` BEFORE reloading.
#   3. Copies deploy/infra/sslh/default -> /etc/default/sslh.
#   4. Reloads nginx and restarts sslh.
#
# Idempotent. Refuses to reload if `nginx -t` fails. Certs in
# /etc/nginx/ssl/ are NOT touched (out-of-band secrets).
set -euo pipefail

INFRA_DIR="${INFRA_DIR:-$(cd "$(dirname "$0")" && pwd)}"
NGINX_SRC="$INFRA_DIR/nginx/arkana-gateway.conf"
SSLH_SRC="$INFRA_DIR/sslh/default"
NGINX_ENABLED="/etc/nginx/sites-enabled"
SSLH_DST="/etc/default/sslh"

echo "==> Applying host config from: $INFRA_DIR"

# --- nginx site config ---
if [[ ! -f "$NGINX_SRC" ]]; then
  echo "!! Missing $NGINX_SRC — capture it from the server first." >&2
  exit 1
fi

echo "==> nginx -t (pre-check)"
nginx -t

echo "==> Installing nginx site config"
mkdir -p "$NGINX_ENABLED"
cp -v "$NGINX_SRC" "$NGINX_ENABLED/arkana-gateway.conf"

echo "==> nginx -t (post-install validation)"
nginx -t

# --- sslh ---
if [[ ! -f "$SSLH_SRC" ]]; then
  echo "!! Missing $SSLH_SRC — capture it from the server first." >&2
  exit 1
fi
echo "==> Installing sslh default"
cp -v "$SSLH_SRC" "$SSLH_DST"

# --- reload ---
echo "==> Reloading services"
systemctl reload nginx
systemctl restart sslh
echo "==> Done. Verify login + TLS on the live endpoint."
