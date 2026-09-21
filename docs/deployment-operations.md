# Deployment & Operations Runbook

> **Current release policy:** For the 2026-09-01 reconciled production lane, use [`release-reconciliation-20260901.md`](release-reconciliation-20260901.md) and [`release-reconciliation-20260901-runbook.md`](release-reconciliation-20260901-runbook.md) as the authoritative release and promotion procedures. The older generic `latest`/`docker compose up` examples below are historical mechanics only; do not use them to promote production.

Canonical guide for deploying the ARKANA GATEWAY to the **gateway-host** server,
reconciling git branches, keeping commit history clean, and maintaining server
hygiene. Companion to `docs/docker-deployment.md` (which covers the compose
stack itself). Last verified: 2026-08-26.

---

## 1. Roles & Topology

| Location | Path | Purpose |
|---|---|---|
| **Local dev** | `C:\Workspace\gateway` | All coding, merging, building. Full git history. |
| **Server deploy** | `~/AI/Gateway` on gateway-host | Build + run ONLY. No coding on the server. |
| **Git remote** | `github.com/AI/gateway` | Source of truth. Auth on the Windows host only. |

**Golden rule:** gateway-host is **deployment-only**. Never edit source on the
server; it exists to `docker compose build` + `up` from a checked-out tree.

### Connection
```bash
ssh -i C:\Users\user\gateway_key -p 1212 deploy-user@gateway-host.arkana.dev
```
- Non-standard SSH port **1212**. User in `docker` + `sudo`.
- The SSH link flaps; wrap long operations in `nohup` + a marker file (§5).

### Network (LAN-only)
- `arkana.dev` → private `10.10.0.10` (gateway-host).
- From the Windows host only `:80` (plain HTTP) and `:443` (HTTPS) are routable;
  `:5011` RESETS cross-subnet. On the server itself, use `localhost:5011`.

---

## 2. Git Identity (MANDATORY)

Every commit's author AND committer MUST be:
```
Hernanda <hernanda-git@users.noreply.github.com>
```
**Forbidden identities** (seen in history, all must be rewritten out):
`Hermes Agent <hermes@noreply.local>`, `Hernanda <hernanda-git@users.noreply.github.com>`,
`Hernanda <dev@arkana.com>`.

Audit any repo/branch set:
```bash
git log --all --format='%an <%ae>|%cn <%ce>' | sort | uniq -c   # expect ONE line
```
Fixing bad identities already in commits requires a **history rewrite**
(filter-branch), not just a re-commit — see §6.

---

## 3. Branch Model

**Single-branch since 2026-08-26:** `main` is the ONLY branch, local and
remote. All feature/fix work follows:

1. `git fetch origin --prune`
2. `git worktree add ../<name> -b <type>/<topic>-<UTCdate> origin/main` (never
   touch another session's worktree; main clone carries protected WIP)
3. Work → quality gate (build 0/0 + suite green) → merge to `main` (no history
   rewrite) → push → delete the branch.

> Historical trap kept for archaeology: this repo previously carried unrelated
> histories and stale WIP branches whose tips were OLDER than main. Before
> merging any resurrected branch, verify it is not a silent revert: compare
> commit dates and diff the branch's touched files against main. A branch with
> unique commits can still be WRONG to merge — check content, not counts.
> Pre-cleanup backup of every deleted ref:
> `C:/Workspace/gateway_pre_final_reconcile_20260826.bundle`.

Map divergence before any merge:
```bash
git branch -vv
git rev-list --count A..B        # commits B has that A lacks
git rev-list --count B..A        # and the reverse
git log -1 --format='%ci %h %s' <branch>
```

---

## 4. Merging Branches (keep ALL work)

1. **Trial first:** `git merge --no-commit --no-ff <src>`; inspect
   `git diff --name-only --diff-filter=U`; then `git merge --abort`.
2. **Unrelated histories:** list unique files each side owns before forcing —
   `git diff --diff-filter=A --name-only A B` (added in B),
   `--diff-filter=D` (only in A). A blind merge can delete files one side
   intentionally removed. Use `--allow-unrelated-histories -X theirs` only after
   auditing, then reconcile leftover files so the tree matches intent.
3. **Resolve to keep both intents:** additive conflicts → keep both lines;
   a file one side rewrote as a superset → `git checkout <src> -- <file>`.
4. **Prove faithfulness:** staged `git write-tree` must equal
   `git rev-parse <branch>^{tree}` when the goal is tree-equality. Then BUILD:
   ```bash
   dotnet build src/Arkana.Gateway.Api/Arkana.Gateway.Api.csproj -c Debug -v q
   ```
   0 errors / 0 warnings is the real proof the merge didn't corrupt code.

---

## 5. Deploy: Local → gateway-host (server tree has NO `.git`)

Since 2026-08-24 the server tree `~/AI/Gateway` is **git-less** (deploy-only).
Transfer the FULL tree as a tarball — never a partial/changed-files-only copy.

```bash
# 1. On local — confirm build green (§4.4), then tar the FULL tree.
cd /c/Workspace/gateway
tar --exclude='*/bin' --exclude='*/obj' --exclude='.git' --exclude='node_modules' \
    --exclude='.hermes' --exclude='tools/antigravity-mitm' -czf /tmp/gw.tar.gz .
# MUST include: root build files (Arkana.slnx, Dockerfile, NuGet.config, ...),
# tests/, and src/Arkana.Gateway.Api/wwwroot/** — missing wwwroot = entire UI
# unstyled while routes still return 200.

# 2. Copy to server.
scp -P 1212 -i C:\Users\user\gateway_key /tmp/gw.tar.gz \
    deploy-user@10.10.0.10:~/

# 3. On server — extract into ~/AI/Gateway (replace content), then build.
tar -xzf ~/gw.tar.gz -C ~/AI/Gateway
```

**Rebuild via nohup + marker** (the SSH link flaps; never block on a long build
inside one SSH call):
```bash
# deploy.sh on the server:
docker build -f ~/AI/Gateway/Dockerfile -t arkana-dev-gateway:latest ~/AI/Gateway
cd ~/arkana-deploy && docker compose up -d --no-deps gateway   # surge gateway ONLY
echo DEPLOY_OK > /tmp/deploy.done
# launch:  nohup bash ~/deploy.sh >~/gw_build.log 2>&1 & echo LAUNCHED
# poll:    ssh ... 'cat /tmp/deploy.done 2>/dev/null' in a retry loop
```

> The image ships compiled DLLs only. Verify what's actually running with
> `docker inspect -f '{{.Created}}' arkana-gateway` vs your commit time.
> Before risky deploys: `pg_dump` backup + `docker commit arkana-gateway
> arkana-rollback:pre-<task>-$ts`. Recreate the gateway container ONLY —
> never postgres/redis/qdrant/n8n/ollama.

---

## 6. Push to Git (from the Windows host)

The Windows host has Git auth (`manager` credential helper). Normal flow:
```bash
git fetch origin --prune
git push origin main
git ls-remote --heads origin                        # expect ONLY main
```
Force-push is reserved for your OWN feature branch before merge — never `main`.

---

## 7. Server Hygiene (deployment-only)

Debug sessions leave debris. Keep the server minimal: repo + dotfiles.

**Safe cleanup (archives debris, purges /tmp, drops bin/obj, gc):**
- Archive home `*.sh/*.py/*.sql/*.conf/*.md/*.csv` to a dated tarball, then remove.
- Purge `/tmp/*_build*.log`, `*.done`, `*_deploy.log`, `*.bundle`.
- Remove gitignored `bin`/`obj` (repo shrinks ~334M → ~8M), `git gc`.

> ⚠️ **NEVER `git clean -fdX` here.** It deletes ALL gitignored files including
> `deploy/.env` (pinned master key + OPENCODE key). Losing it breaks every
> sealed provider key (`Failed to unwrap DEK`). Back up `deploy/.env` first and
> remove `bin`/`obj` explicitly with `find ... -prune -exec rm -rf {} +`.
> Dry-run `git clean -fdnX` to see what would go.

> Preserve live nginx TLS config under `/etc/nginx/sites-available/`
> (`arkana-gateway.conf`) and certs under `/etc/nginx/ssl/gateway.arkana.dev/`.
> apache2 is DEAD since 2026-08-24 (nginx owns :443 via sslh) — do not revive.

---

## 8. `deploy/.env` — critical secrets

Gitignored, holds:
- `ARKANA_MASTER_KEY` — envelope-encryption master key. If it changes/regens,
  all sealed provider keys throw `Failed to unwrap DEK`. Pin it (see
  `docs/key-management.md`).
- `OPENCODE_GO_API_KEY` — upstream opencode.ai bearer.
- `N8N_ENCRYPTION_KEY` — must match the persisted n8n volume or n8n crash-loops.

Changing any value requires `docker compose up -d <svc>` (NOT `restart` — that
reuses the old env).

---

## 9. Verification (one pass, from the server)

```bash
ssh -i C:\Users\user\gateway_key -p 1212 deploy-user@10.10.0.10 \
  'docker inspect -f "{{.State.Status}} {{.Created}}" arkana-gateway && \
   curl -s http://127.0.0.1:5011/health && echo && \
   for r in / /profile /docs /key-pools /agents; do \
     curl -s -o /dev/null -w "$r %{http_code}\n" http://127.0.0.1:5011$r; done && \
   curl -s -o /dev/null -w "auth-gate %{http_code}\n" -X POST \
     http://127.0.0.1:5011/v1/chat/completions -H "Content-Type: application/json" -d "{}" && \
   docker logs arkana-gateway --since 24h 2>&1 | grep -c "Failed to unwrap DEK"'
```
**Expect:** container `running`, `.Created` newer than your last merged commit;
health JSON `healthy`; UI routes `200`; `/v1` no-key `401`; unwrap errors `0`.

Admin probes from the server itself use loopback + `X-Admin-Key`
(`http://127.0.0.1:5011/admin/*`) — HTTPS-via-nginx from the server flakes
(curl exit 60). Public route checks go through `https://gateway.arkana.dev`.

---

## 10. Known bug class: Blazor UI page returns `{"error":"API key required"}`

The Blazor dashboard and the `/v1` API share one app. `Program.cs` exempts UI
routes from `ApiKeyAuthMiddleware` via an `app.UseWhen(...)` allow-list. **Any UI
page route missing from that list** is caught by the API-key middleware and
renders `{"error":"API key required"}` as the full page.

Fix: add `!path.StartsWith("/<route>") &&` to the `UseWhen` predicate in
`src/Arkana.Gateway.Api/Program.cs`. Audit all UI routes:
```bash
# list every Blazor page route, then cross-check against the UseWhen list
grep -rh '@page "' src/Arkana.Gateway.Api/Components/Pages/ | sort -u
```
`ApiKeyAuthMiddleware` itself only auto-skips `/health` and `/admin`; everything
else depends on the allow-list. (`/key-pools` was the missing one, 2026-07-19.)
