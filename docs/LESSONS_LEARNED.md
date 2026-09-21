# Lessons Learned — ARKANA GATEWAY

> Living operational notes. Source-of-truth order: current source and fresh runtime evidence, Git history, curated plans/reports, then chat history.

## Environment and deployment

- Work in an isolated checkout/worktree per task and start from a freshly fetched `origin/main`; parallel lanes must never share a working directory.
- The production server source tree is git-less. Deploy parity requires a full-tree manifest or exact per-file comparison; an extraction-only overlay can retain deleted files.
- Deploy only the gateway container. PostgreSQL, Redis, Qdrant, n8n, Ollama, volumes, and deployment `.env` must remain untouched unless an explicitly approved incident requires otherwise.
- A `docker compose up --build` can reuse stale layers. When source behavior does not change, use the documented no-cache build and verify the container image creation time.
- Prefer explicit `git add <paths>`. A blanket `git add -A` can drag unrelated in-progress work into a commit when several workstreams share a checkout.

## Authentication and Blazor

- A successful login page HTTP 200 and an anonymous protected-route 302 do not prove authenticated Blazor rendering.
- Blazor state changes from background callbacks must be dispatched through the renderer (`InvokeAsync(StateHasChanged)`). Calling `StateHasChanged` from a thread-pool continuation can terminate or destabilize the circuit.
- Dashboard hydration must not block the post-login document response. Optional analytics, n8n, provider health, and full log payloads are background/secondary work.
- Login forms can carry an antiforgery token bound to a stale claims identity when another tab completes login. Fresh no-cache login state and stale-cookie invalidation prevent recurring HTTP 400 loops.
- A cookie rotation is a safe way to invalidate stale browser sessions after an authentication/circuit contract change; it avoids asking users to manually clear cookies.

## Cost and usage UI

- API-key filtering must use the same key identity that the token tracker persists. Resolving IDs from a separate table without a tracker-side key identity can create a silent no-op filter.
- Chart colors must be deterministic across months and filters. Sequential palette assignment makes the same model change color depending on query order.
- UI loading and error states need separate state transitions. A failed request must not look like an empty successful result.
- Codex usage has two distinct windows: a primary approximately five-hour window and a secondary weekly window. Store observations per account and window, not only as a global aggregate.
- Usage snapshots are best-effort telemetry. A provider throttle or a canceled request must not break inference or make the profile page crash.

## ChatGPT Codex / OpenCode

- A real OpenCode → Gateway → stored Codex subscription request is stronger evidence than a synthetic curl. Verify both gateway activity and a client-level response.
- A gateway log containing HTTP 200 responses plus `TaskCanceledException` in the translation pump can mean the client canceled after receiving headers; it is not automatically an upstream outage.
- Account selection and upstream calls need bounded timeouts. Without them, an unavailable/throttled OAuth account can make an agent appear to hang forever.
- Never infer API-key validity from a historical report. Recheck fresh request activity and sanitized logs. Never print raw keys or tokens.
- Stored API keys are hashed and cannot be recovered from the database. Do not mint/rotate a replacement merely to make a diagnostic pass unless explicitly authorized.

## Git reconciliation

- A branch described as “merged” in an old report may no longer be an ancestor of the current main tip. Always run `git merge-base --is-ancestor` against fresh `origin/main`.
- Reconcile coherent workstreams with explicit cherry-picks. Do not merge broad WIP snapshots that change hundreds of unrelated files.
- Preserve later main fixes when importing an older branch. Resolve overlapping files by intent, never global `ours`/`theirs`.
- Required identity for new commits: `Hernanda <hernanda-git@users.noreply.github.com>` for author and committer.
- Main publication is fast-forward only after Release build, full suite, whitespace check, and remote-tip read-back.

## Historical verification snapshot (2026-09-01, HISTORICAL)

Kept as a record of *what was checked*, not as current behaviour. Regenerate every count and
re-run the flow against your own deployment before relying on any of it.

- Release gate at the time: 0 warnings, 0 errors, full suite green.
- Authenticated-flow acceptance used: login GET 200 -> login POST 302 -> auth cookie issued ->
  authenticated dashboard 200 -> authenticated Blazor negotiate 200.
- Security boundaries checked: admin route 200 only with admin auth, key-less `/v1` 401, master key
  present, no envelope-encryption unwrap errors in the logs.
- A rollback image digest and a database dump were retained before promotion.
- `deploy-hook.service` was intentionally disabled: the hook failed open and had no release gates.

## 2026-09-01 reconciliation lessons

- Login success is not dashboard success. The decisive path is fresh login → auth cookie → protected document → authenticated negotiate → WebSocket where available.
- Selective feature integration is safer than cherry-picking a broad branch built on a regressed base. Protect lifecycle, auth, DI, and deployment surfaces explicitly.
- Minimal API endpoint discovery can fail before runtime if service dependencies are inferred as body parameters. Use explicit `[FromServices]` annotations and keep endpoint discovery tests in the gate.
- A release gate that reads only the last test-project summary undercounts a multi-project suite. Aggregate all project summaries and assert the total floor.
- Mutable tags such as `latest` are not release identity. Promotion requires an image digest and a matching approval marker.
- A deployment hook being disabled does not prove that all deployment paths are disabled. Audit systemd units, compose access, manual scripts, and image creation timestamps.
- The production compose project and build source can be separate directories. Transfer an exact clean source archive and never assume syncing only `deploy/` changes the application binary.
- A first promotion wrapper can contain a false negative. When rollback triggers, reproduce the exact failed checkpoint before changing application code; here the valid `.Arkana.Auth.v2` cookie was incorrectly checked as `.AspNetCore.Cookies`.
