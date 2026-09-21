# Phase 6 Gemini broker operations runbook

This is an opt-in, non-destructive deployment artifact. It does not change
`deploy/docker-compose.yml`, `deploy/.env`, or any running service. Commands
below assume the repository root is the current directory and Docker access is
authorized.

## Preflight

```bash
# PASS: inspect only; must show exactly two services and the pinned digest.
docker compose -f deploy/docker-compose.gemini-broker.yml config
# PASS: verify the image reference without starting a container.
grep -F 'eceasy/cli-proxy-api@sha256:238691ac26ce55e4d1c5219d72e3ad74838f81eda26359912eeb415e2820d163' deploy/docker-compose.gemini-broker.yml
# PASS: confirm no host management/data ports are published.
! docker compose -f deploy/docker-compose.gemini-broker.yml config | grep -E '^ *ports:'
# BLOCKED: if `docker compose config` fails, the image/config schema is not approved.
```

Before activation, compare the pinned image's configuration help with the
slot templates. `max-retry-credentials=1`, `session-affinity=false`, and
`remote-management.allow remote=false` must be accepted; a rejected key is a
hard `BLOCKED`, not a reason to continue with defaults. Inject management keys
and OAuth state through the private volume only. Never put them in Git, shell
history, or command output.

## Optional activation (operator approval required)

```bash
# PASS: only after the base internal network exists and private config volumes
# have been seeded from deploy/gemini-broker/config/slot-{a,b}.yaml.
docker compose -f deploy/docker-compose.gemini-broker.yml --profile gemini-broker up -d
./scripts/gemini-broker-health.sh
# BLOCKED: do not run if this would recreate/alter a live production service.
```

The overlay has no `ports:` entries. Management is internal-only via the
Docker network. Keep the slot count at two unless a separately reviewed change
updates the bounded configuration and isolation tests.

## Backup (auth state only)

```bash
# PASS: creates mode-600 archives and mode-600 SHA-256 sidecars; prints no data.
BACKUP_DIR=./backups/gemini-broker ./scripts/gemini-broker-backup.sh
# BLOCKED: stop and investigate if Docker cannot read either auth volume.
```

Backups contain authentication state and are sensitive. Store them outside Git
with restricted filesystem and remote-storage permissions.

## Restore / rollback

Restore is intentionally refusal-first: checksum must match, the target slot
must not be running, and the archive must be explicitly selected.

```bash
# PASS: verify archive, then restore one stopped slot only.
BACKUP_ARCHIVE=./backups/gemini-broker/gemini-slot-a-auth-<UTC>.tar.gz \
  SLOT=a ./scripts/gemini-broker-restore.sh
# BLOCKED: a running target, missing sidecar, or checksum mismatch aborts.

# PASS: rollback broker artifacts without touching the base stack.
docker compose -f deploy/docker-compose.gemini-broker.yml \
  --profile gemini-broker down --remove-orphans
# BLOCKED: never use `docker compose down -v` during rollback; it destroys
# persistent slot auth/config volumes.
```

## Verification

```bash
# PASS: static model and safety checks.
docker compose -f deploy/docker-compose.gemini-broker.yml config >/dev/null
./scripts/gemini-broker-health.sh
# PASS: inspect status only; never print environment or mounted files.
docker compose -f deploy/docker-compose.gemini-broker.yml \
  --profile gemini-broker ps
# BLOCKED: no live evidence is claimed by this repository until an operator
# runs these commands against the intended host and records sanitized output.
```
