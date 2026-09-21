# CI/CD — Branch Protection & Status Checks

> **Updated 2026-08-26:** the repo is now **single-branch (`main`)** — `dev`
> was deleted in the final reconciliation. Branch protection applies to `main`
> only. Feature work runs on short-lived branches merged to `main` (see
> `deployment-operations.md` §3); direct pushes to `main` by the maintainer
> remain the practical deploy path since CI runs on Git are not configured.

This document specifies the **required status checks** that must pass before
`main` accepts merge commits. Configure these under
**Settings → Branches → Branch protection rules** in Git.

---

## Recommended `main` rule

| Setting | Value |
|---------|-------|
| Require a pull request before merging | optional (solo-maintainer repos may allow direct pushes with green gates) |
| Require status checks to pass before merging | ✅ |
| Require branches to be up to date before merging | ✅ |
| Include administrators | ✅ (no one bypasses CI) |
| Block force pushes | ✅ |
| Block deletions | ✅ |

---

## Required status checks

The `ci.yml` workflow exposes these named jobs — the names must match the
**job names** below (not step names):

| Job name | Required on `main` | Notes |
|----------|--------------------|-------|
| `Build (ubuntu-latest)` | ✅ | Restore + `dotnet build` with `TreatWarningsAsErrors=true` |
| `Test + Coverage` | ✅ | All 5 test projects; 1195 tests must pass |
| `Coverage Gate` | ✅ | Fails if line coverage < **70%** (configurable via `COVERAGE_THRESHOLD` env) |
| `Docker Build` | ✅ | Builds the gateway image; pushes to GHCR on `v*` tags |

> The `(ubuntu-latest)` suffix in `Build (ubuntu-latest)` is added by GitHub
> Actions automatically because we declared a `matrix.os` strategy. If you
> add a Windows runner later, you'll need to add `Build (windows-latest)` to
> the required list.

---

## Secrets

| Secret | Required? | Purpose |
|--------|-----------|---------|
| `GITHUB_TOKEN` | auto | GHCR login (injected automatically by GitHub Actions for any job with `packages: write`) |
| `CODECOV_TOKEN` | optional | Upload coverage to codecov.io for trend tracking |
| `SONAR_TOKEN` | optional | SonarCloud integration |

No manual secret rotation is required for the current pipeline.

---

## Release flow

1. Feature branch → quality gate locally (build 0/0, suite ≥907) → merge/PR into `main`.
2. Deploy from `main` via the tar→scp→docker build runbook (`deployment-operations.md` §5).
3. Tag a release from `main`: `git tag v1.2.3 && git push --tags`.
4. The `docker` job pushes `ghcr.io/<org>/ai-gateway:1.2.3`,
   `:1.2`, `:1`, and the short SHA tag to GHCR.

---

## Local reproduction of CI

Run the same commands the CI runs:

```bash
# 1. Build (Release, warnings as errors)
dotnet build Arkana.slnx -c Release /p:TreatWarningsAsErrors=true

# 2. Test + coverage (all 5 projects)
for proj in tests/Arkana.*.Tests; do
  dotnet test "$proj" \
    -c Release \
    --settings coverlet.runsettings \
    --collect:"XPlat Code Coverage" \
    --results-directory ./TestResults
done

# 3. Generate coverage report
dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator \
  -reports:"TestResults/**/coverage.cobertura.xml" \
  -targetdir:"./coverage" \
  -reporttypes:"Html_Dark;TextSummary"

# 4. View summary
cat ./coverage/Summary.txt

# 5. Build Docker image
docker build -t arkana:local -f Dockerfile .
```
