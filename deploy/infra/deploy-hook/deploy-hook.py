#!/usr/bin/env python3
"""
deploy-hook.py — Git webhook receiver for the gateway deploy dir.

Runs on gateway-host as a systemd service, listening on 127.0.0.1:9000.
nginx proxies /deploy-hook -> this. The Git host sends a webhook on push; we:
  1. Verify the X-Deploy-Secret header matches DEPLOY_HOOK_SECRET.
  2. Ignore anything that is not refs/heads/main (no auto-deploy).
  3. Fetch the main branch archive from the Git host (needs GIT_TOKEN, repo is
     private). Extract only the deploy/ folder.
  4. Back up the current arkana-deploy/ on the server, rsync deploy/ in.
  5. Run `docker compose up -d` so env changes take effect.

Safe: only touches arkana-deploy/; never deletes unrelated files.
"""

import json
import os
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer

HOST = "127.0.0.1"
PORT = 9000
SECRET = os.environ.get("DEPLOY_HOOK_SECRET", "")
GIT_TOKEN = os.environ.get("GIT_TOKEN", "")
ARCHIVE_URL = "https://github.com/hernanda-git/arkana-gateway/archive/main.tar.gz"
DEPLOY_DIR = "/home/deploy-user/arkana-deploy"
SERVICE = "gateway"  # compose service to recreate

LOG = lambda m: print(f"[deploy-hook] {m}", flush=True)


def verify_secret(headers: dict) -> bool:
    if not SECRET:
        return True  # dev: allow unauthenticated if no secret set
    return headers.get("X-Deploy-Secret", "") == SECRET


def fetch_and_extract_deploy() -> bool:
    req = urllib.request.Request(ARCHIVE_URL)
    if GIT_TOKEN:
        req.add_header("Authorization", f"token {GIT_TOKEN}")
    with tempfile.TemporaryDirectory() as tmp:
        tar_path = os.path.join(tmp, "main.tar.gz")
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                data = r.read()
            with open(tar_path, "wb") as f:
                f.write(data)
        except Exception as e:  # noqa: BLE001
            LOG(f"archive fetch failed: {e}")
            return False
        # Archive top dir is like "arkana-gateway-main-<hash>/deploy"
        extract_dir = os.path.join(tmp, "ext")
        os.makedirs(extract_dir, exist_ok=True)
        try:
            with tarfile.open(tar_path) as tf:
                members = [m for m in tf.getmembers()
                           if m.name.split("/", 2)[1:2] == ["deploy"]
                           or m.name.count("/") >= 1 and m.name.split("/", 1)[1].startswith("deploy/")]
                # simpler: extract all, then locate deploy/
                tf.extractall(extract_dir)
        except Exception as e:  # noqa: BLE001
            LOG(f"tar extract failed: {e}")
            return False
        # find deploy dir inside extracted tree
        found = None
        for root, dirs, _ in os.walk(extract_dir):
            if os.path.basename(root) == "deploy" and os.path.exists(
                    os.path.join(root, "docker-compose.yml")):
                found = root
                break
        if not found:
            LOG("deploy/ not found in archive")
            return False
        # backup
        backup = f"{DEPLOY_DIR}.bak-{subprocess.check_output(['date', '+%Y%m%d-%H%M%S']).decode().strip()}"
        subprocess.run(["cp", "-r", DEPLOY_DIR, backup], check=True)
        LOG(f"backed up to {backup}")
        # copy deploy contents into arkana-deploy.
        # CRITICAL: never touch .env on the server (it holds secrets that are
        # git-ignored and absent from the archive). We sync only files that
        # exist in the repo's deploy/ — no --delete, so server-only files
        # (.env, *.bak) are preserved.
        if subprocess.run(["which", "rsync"], capture_output=True).returncode == 0:
            subprocess.run(["rsync", "-a", "--exclude", ".env",
                            "--exclude", "*.bak-*",
                            f"{found}/", f"{DEPLOY_DIR}/"], check=True)
        else:
            for item in os.listdir(found):
                s = os.path.join(found, item)
                d = os.path.join(DEPLOY_DIR, item)
                if os.path.isdir(s):
                    subprocess.run(["cp", "-r", s, d], check=True)
                else:
                    subprocess.run(["cp", s, d], check=True)
        LOG("deploy/ synced")
        return True


def apply_compose():
    try:
        subprocess.run(["docker", "compose", "-f",
                        f"{DEPLOY_DIR}/docker-compose.yml", "up", "-d", SERVICE],
                       cwd=DEPLOY_DIR, check=True)
        LOG("docker compose up -d gateway OK")
    except subprocess.CalledProcessError as e:
        LOG(f"compose failed: {e}")


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):  # noqa: N802
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        if not verify_secret(dict(self.headers)):
            self.send_response(403)
            self.end_headers()
            return
        try:
            payload = json.loads(body or b"{}")
        except json.JSONDecodeError:
            payload = {}
        ref = payload.get("ref", "")
        LOG(f"webhook ref={ref}")
        if ref != "refs/heads/main":
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'{"status":"ignored","reason":"not main"}')
            return
        if fetch_and_extract_deploy():
            apply_compose()
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'{"status":"deployed"}')
        else:
            self.send_response(500)
            self.end_headers()
            self.wfile.write(b'{"status":"failed"}')

    def log_message(self, *args):  # silence default logging
        pass


if __name__ == "__main__":
    LOG(f"listening on {HOST}:{PORT}")
    HTTPServer((HOST, PORT), Handler).serve_forever()
