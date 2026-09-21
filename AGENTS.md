# AGENTS.md — ARKANA GATEWAY

> Rules for EVERY AI agent session (Hermes, Claude Code, Codex, OpenCode, dst)
> working in this repo. Goal: **parallel multi-session work with zero mutual
> damage**, all lanes converging into `main`.
> Companion: `CLAUDE.md` (build/test commands) · `docs/session-context-arkana-gateway-20260826.md` (project primer).

---

## 0. Golden rules (TL;DR)

1. **`main` is sacred and single.** Never commit to it directly from a task
   worktree; never force-push it; never reset it.
2. **One session = one worktree = one branch.** Never share a working
   directory with another lane.
3. **The main clone `C:/Workspace/gateway` is a protected area** — its dirty
   WIP belongs to whoever owns it; other sessions must not stage/commit/
   discard/rebase it.
4. **Isolation is cheap.** Duplicate full checkouts on disk are FINE and
   expected. Disk space is not a concern; broken parallel work is.
5. **Merge order is claim → rebase → gate → merge → delete.** A lane that
   finishes second rebases onto the new main before merging.

---

## 1. Branch naming convention

```
<type>/<topic>-<UTCdate>[<-seq>]
```

| Segment | Values | Notes |
|---|---|---|
| `type` | `feat` `fix` `docs` `chore` `refactor` `audit` `reconcile` | Conventional-commit vocabulary |
| `topic` | kebab-case, ≤5 words | e.g. `profile-page`, `sol-stream-truncation` |
| date | `YYYYMMDD` UTC | e.g. `20260826` |
| seq | `-2`, `-3`, … | Only if same topic+date collides |

Examples: `feat/profile-page-20260826`, `fix/dek-unwrap-20260827-2`.

**Forbidden patterns:**
- No personal names as branches (`user-branch`) — lanes are identified by
  branch name + worktree dir, not people.
- No bare `dev`, `wip-*`, `test` — these caused the 2026-07 unrelated-history
  disaster. `main` must remain the only long-lived branch.
- No branch without a date — stale branches are untraceable.

## 2. Worktree convention (per-session isolated workspace)

Every task starts by creating its own full checkout:

```bash
cd C:/Workspace/gateway          # main clone (protected area)
git fetch origin --prune         # ALWAYS fresh base
git worktree add ../gw-<lane> -b <type>/<topic>-<UTCdate> origin/main
cd ../gw-<lane>
```

- Worktree directory naming: `C:/Workspace/gw-<lane>` where `<lane>` =
  short topic (`gw-profile-ui`, `gw-rate-limiter`). Keep it ≤20 chars.
- Each worktree gets its own `bin/`, `obj/`, NuGet restore — **do not try to
  share build outputs between worktrees** (.NET does not support it safely).
- List before touching anything: `git worktree list`. Another session's
  worktree is off-limits: no checkout/reset/stash/clean/branch-delete in it.
- Remove your own worktree when your lane is merged:
  ```bash
  cd C:/Workspace/gateway && git worktree remove ../gw-<lane> --force
  git branch -D <your-branch>
  ```
  `--force` is allowed ONLY on your own worktree with zero uncommitted files
  you still need.

## 3. File-level conflict avoidance inside the repo

Parallel lanes WILL overlap pages/files sometimes (e.g. two agents editing
Blazor pages). To make that safe:

1. **Claim your surface at lane start.** Post in chat / note in your report
   which files you will touch (e.g. "this lane touches Profile.razor +
   DashboardService.cs"). Two lanes may not claim the same file simultaneously;
   the second lane either waits or works on a different file set.
2. **UI pages:** one page per lane. If two lanes must edit the SAME page,
   sequence them, don't race.
3. **Shared services** (`DashboardService.cs`, `Program.cs`,
   `ResponsesEndpoints.cs`): additive-only edits while another lane holds them;
   if your change would rewrite the same region, wait for the other lane to
   merge first.
4. **Never** resolve a merge conflict by discarding the other lane's changes.
   If a conflict looks like "my change vs their change" and both look correct,
   STOP and ask the user.

## 4. Merge protocol into `main` (anti-backward-progress)

```bash
# in your worktree, when done:
git fetch origin --prune                      # did main move?
git rev-list --count origin/main..HEAD        # my unique commits
git log HEAD..origin/main --oneline           # what landed meanwhile

git rebase origin/main                        # replay mine on top
# conflicts: MY code wins in files I claimed; THEIR code wins elsewhere.
# Unresolvable => STOP, report, do not force.

dotnet build Arkana.slnx -c Release --nologo -v q    # expect 0 warn/0 err
dotnet test  Arkana.slnx -c Release --no-build       # expect >= current baseline
```

Then push & fast-forward main:

```bash
git push -u origin <branch>                   # backup your lane first
git fetch origin && git push origin <branch>:main     # only if FF possible
# if non-FF: re-run §4 from the top (main moved again mid-merge)
```

After merge: verify `git log origin/main -1` shows your commit, then clean up
(§2). The user may also request PR-style review — in that case leave the
branch pushed and stop.

**Baseline rule:** the test floor is whatever the last green run reported
(currently ≥1173). If main's suite count DROPPED after your merge, you regressed
it — fix or revert immediately.

## 5. Quality gates (every lane, no exceptions)

| Gate | Command | Pass condition |
|---|---|---|
| Build | `dotnet build Arkana.slnx -c Release --nologo -v q` | 0 warnings, 0 errors |
| Suite | `dotnet test Arkana.slnx -c Release --no-build` | 0 failed; total ≥ last known |
| Whitespace | `git diff --check` | clean |
| Identity | `git log -1 --format='%an <%ae>'` | `Hernanda <hernanda-git@users.noreply.github.com>` |
| Scope | `git show --stat HEAD` | only files you claimed |

Commit style: conventional (`feat:`/`fix:`/`docs:`/`chore:`), atomic, explicit
`git add <file>` — **never `git add -A`/`git add .`** (drags other lanes' WIP).

## 6. Protected areas (do-not-touch unless explicitly assigned)

| Path | Owner |
|---|---|
| `tools/antigravity-mitm/**` dirty files | antigravity client lane |
| `README.md` (when dirty in main clone) | whichever lane made it dirty |
| `deploy/**`, `.env*`, secrets | infra/user only |
| DB writes outside your own test keys | forbidden for all agent lanes |

Ephemeral admin keys: mint → use → DELETE (204) within the same lane.

## 7. Multi-session etiquette (Hermes/cron/subagents)

- Before ANY destructive op (branch delete, image prune, container recreate):
  re-fetch and re-list fresh state — parallel actors may have changed things
  since your last observation.
- Long server-side builds: `nohup … > ~/gw_build.log 2>&1 &` + poll pattern;
  never block one SSH call on a long build.
- One deploy at a time. If another lane deployed <30 min ago and hasn't
  verified, coordinate before recreating the gateway container.
- Report style stays terse ID/EN with tables; every lane ends with
  acceptance-vs-result evidence, SHAs, and remaining open items.

## 8. Recovery playbook

| Situation | Action |
|---|---|
| My worktree corrupted | `git status` to salvage patches → new worktree → re-apply; never reuse a half-broken tree |
| Main moved past my base | rebase per §4 |
| Accidental commit on wrong branch | `git branch <correct>` + reset wrong branch back — never rewrite main |
| Lost lane after crash | check `git worktree list` + `git branch -a`; every committed SHA survives until GC |
| Suspected silent revert after merge | diff merged files vs pre-merge ref; the pre-reconcile bundles (`gateway_pre_final_reconcile_*.bundle`) are ground truth |
