# Operations runbook — ARKANA GATEWAY

Day-2 procedures for running the gateway, in the order you will need them.
Companion documents: [`deploy/README-GATEWAY.md`](../deploy/README-GATEWAY.md) (install,
configuration, troubleshooting) and [`deploy/gemini-broker/README.md`](../deploy/gemini-broker/README.md)
(Gemini subscription broker slots).

Nothing in here contains secrets: read them from the deploy host's `.env` or your
secret store.

---

## Environments

| Environment | What runs | How it is reached |
|---|---|---|
| Local | `deploy/docker-compose.yml` (`arkana-dev` stack) | `http://localhost:5011` |
| Staging | same compose, separate `.env`, own image tag | staging hostname (HTTPS via the reverse proxy) |
| Production | same compose, own `.env`, promoted by digest | `https://gateway.arkana.dev` |

Both server environments deploy from a checkout of `main`; **the running container is
identified by image digest**, never by the git branch alone. Always record the digest
before and after a release.

```bash
docker inspect -f '{{.Config.Image}} {{.Image}} {{.State.Health.Status}}' arkana-gateway
docker exec arkana-postgres psql -U arkana -d arkana -Atc \
  'SELECT count(*) FROM "__EFMigrationsHistory";'
```

---

## Release & rollback

### 1. Build the candidate

```bash
git fetch --prune && git log --oneline -1 origin/main
docker build -t <registry>/gateway:candidate-<sha> .
```

### 2. Smoke the candidate before it touches live

Run the candidate against a **clone** of the production database (never the live DB):

```bash
# dump, restore into a scratch database, run the candidate container against it
docker exec arkana-postgres pg_dump -U arkana -d arkana | \
  docker exec -i <scratch-postgres> psql -U arkana -d arkana_smoke
docker run --rm --network <net> -e ConnectionStrings__Postgres=<scratch> <candidate-image>
```

Then verify with real calls: non-stream, streaming (`data: [DONE]`), one tool call, and
one request per provider family you are about to serve.

### 3. Promote by digest

```bash
docker tag <registry>/gateway:candidate-<sha> <registry>/gateway:latest
docker compose up -d --no-build --no-deps gateway
```

Keep the previous image under a rollback tag first:

```bash
docker tag <current-digest> <registry>/gateway:rollback-pre-<sha>-<timestamp>
```

### 4. Verify after promotion

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://<host>/health
docker logs arkana-gateway --since 10m 2>&1 | grep -E 'fail:|crit:' | head
docker exec arkana-postgres psql -U arkana -d arkana -Atc \
  'SELECT count(*) FROM "__EFMigrationsHistory";'          # must not go backwards
```

Plus one authenticated end-to-end call with an **ephemeral** key that is deleted in the
same run (`DELETE /admin/api-keys/{id}` → 204).

### 5. Rollback

```bash
docker tag <registry>/gateway:rollback-pre-<sha>-<timestamp> <registry>/gateway:latest
docker compose up -d --no-build --no-deps gateway
```

Database migrations are additive in this project, so a rollback does **not** roll the
schema back — that is expected and safe. If a migration must be reverted, restore the
pre-release dump instead.

---

## Schema baseline (why fresh installs do not run the full migration history)

The migrations in `src/Arkana.Infrastructure/Migrations` cannot reproduce the production
schema on an empty database — some were rewritten after they had been applied, and
`OAuthPendingFlows` has no creating migration left in the repository. A fresh database is
therefore initialised from a baseline dump instead, and its migration history is stamped so
EF applies only migrations written after the baseline.

| File | Role |
|---|---|
| `deploy/db/baseline-schema.sql` | Schema dump of the running production database (schema only, no data, no owners/privileges). |
| `deploy/db/baseline-seed.sql` | Reference rows the gateway cannot boot without (the default tenant). No production data, no credentials. |
| `deploy/db/baseline-migrations.txt` | Migration ids already applied to the baseline. |
| `deploy/db/init-baseline.sh` | One-shot initializer, wired into the compose as `db-baseline`; the gateway waits for it. Exits immediately when a history exists, refuses to touch a populated database without one. |

Regenerate the baseline when the schema changes materially (after a release that adds
migrations, or when onboarding a rebuilt environment):

```bash
docker exec -i <postgres> pg_dump -U arkana -d arkana --schema-only \
  --no-owner --no-privileges --schema=public > deploy/db/baseline-schema.sql
# strip pg_dump guard lines and relax the schema statement
sed -i '/^\\restrict\|^\\unrestrict/d; s/^CREATE SCHEMA public;$/CREATE SCHEMA IF NOT EXISTS public;/' deploy/db/baseline-schema.sql
docker exec -i <postgres> psql -U arkana -d arkana -Atc \
  'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";' \
  | sed '1i # Migration ids already applied to the baseline schema (production history, in order).' \
  > deploy/db/baseline-migrations.txt
```

Then verify a from-scratch install still comes up: `docker compose down -v` (a scratch host
or a throwaway copy of the compose) and `docker compose up -d --build` — the gateway must
reach `(healthy)` and the `db-baseline` container must exit 0.

---

## Configuration checks after any compose change

Compose edits are the most common source of "it worked yesterday" failures, because a
lost line silently changes runtime behaviour. After every edit, assert the effective
container environment instead of trusting the file:

```bash
docker exec arkana-gateway printenv | grep -E '^(ARKANA_|ADMIN_|Gemini|CLIPROXYAPI_)' | sort
docker compose config >/dev/null && echo 'compose file parses'
```

The gateway also self-reports the gemini-subscription/broker wiring at startup; look for
a `GeminiBrokerConfig` warning in the logs right after boot. A missing
`GeminiSubscription__ProviderId` with broker slots configured is that warning, and it
manifests as `503 Gemini subscription streaming failed.` on streaming while
non-streaming keeps working.

---

## Gemini subscription broker slots

One container per Google identity. Full mechanics in
[`deploy/gemini-broker/README.md`](../deploy/gemini-broker/README.md).

### Add a slot

```bash
cd <deploy-dir>/gemini-slots
./add-slot.sh <slot> --wire      # or: ./add-slot.sh b c d --wire
```

This generates a management key, renders the broker container (own config + auth
volume, no host ports), starts it, wires `GeminiBroker__Slots__<slot>__BaseUrl` /
`__ManagementKey` into the gateway compose and recreates the gateway. `--wire` then
asserts the recreated gateway carries the slot **and** `GeminiSubscription__ProviderId`.

### Attach an account

Dashboard → `/providers` → *Add account* → choose the slot → complete the Google
consent. Two rules:

1. **Finish the consent in one go.** The broker aborts the flow after a few minutes
   (`oauth flow timed out` in its logs). An interrupted flow still saves a token, but
   without Antigravity onboarding — see the next section.
2. One slot, one Google identity. To use a second subscription, add a second slot.

### Locking a slot down

Brokers accept a data-plane key; the gateway sends it when the slot has one.

- Broker side: `api-keys: ["<key>"]` in the slot's `config.yaml`.
- Gateway side: `GeminiBroker__Slots__<slot>__DataPlaneKey=<key>` (falls back to the
  slot `ManagementKey` when unset, which is what `add-slot.sh` wires).

With a key configured on both sides, calls from outside the compose network are
rejected instead of served.

### Recovering a credential that answers 400/403

Symptom — every call through that account fails, non-stream with
`400 Gemini subscription upstream request failed (HTTP 400)`, stream with
`503`, and the broker logs either

```
antigravity auth missing project_id: request failed with status 403
```
or
```
Cloud Code Private API has not been used in project <client-project> ...
```

Cause: the sign-in for that slot was interrupted, so the stored token never completed
onboarding. Repair options, in order of preference:

1. **Redo the sign-in** from the dashboard for that account (delete the account's
   credential first if the broker refuses to start a new flow) and let the consent
   finish without interruption.
2. **Seed from a healthy slot that holds the same Google account** — copy the working
   `antigravity-*.json` credential into the broken slot's auth volume and restart that
   slot (keep a backup; the two brokers will then refresh independently, which is safe
   for the same identity):

   ```bash
   docker cp <healthy-slot>:/root/.cli-proxy-api/<file>.json ./cred.json
   docker cp ./cred.json <broken-slot>:/root/.cli-proxy-api/<file>.json
   docker exec <broken-slot> sh -c 'chmod 600 /root/.cli-proxy-api/<file>.json'
   docker restart <broken-slot>
   ```

3. **Only if the account is broken beyond use:** remove the account in the dashboard
   (deletes the broker-side credential) and add it again on that slot.

After any of these, verify with a direct broker call and then through the gateway
(non-stream, stream, tools).

---

## Dashboard form pitfalls (bug class)

Buttons that gate on a text field stay disabled while you type unless the field binds
on `input`: Blazor's default `@bind` commits on `change` (i.e. on blur). Every gated
form in this repo now uses `@bind:event="oninput"`. When adding one:

```razor
<input type="text" @bind="model" @bind:event="oninput" />
<button disabled="@(string.IsNullOrWhiteSpace(model))" @onclick="Submit">Save</button>
```

Related: page-local CSS class names must not collide with the global `app.css`. A
`.log-detail-row { display: none }` rule intended for one page silently hid a panel on
another (rendered 0×0). Namespace page-specific classes (the profile page uses `pf-*`).

---

## Request logs and the profile page

- `/logs` and `/profile` browse **scalar-only** rows with SQL-side paging
  (`SearchSummariesPageAsync`); the heavyweight `MessagesJson`/`ToolCallsJson` payloads
  are hydrated only when a log entry is opened (`GetOwnedLogByIdAsync`, ownership
  enforced server-side). Keep that split when touching log queries — loading full
  payloads for a list view is what made the page slow.
- `RequestLogs` rows for broker-managed traffic carry `RouteKind = "broker-managed"` and
  the resolved account code, which is how to tell which slot served a request.

---

## Known pre-existing conditions (not regressions)

| Condition | Notes |
|---|---|
| Startup prints `Error: libgssapi_krb5.so.2: cannot open shared object file` | Benign runtime note: Kerberos/GSSAPI is not in the image (the runtime stage avoids `apt` on purpose). Only relevant for GSSAPI database auth. |
| A migration can be invisible to EF Core | If a migration file has neither a generated `.Designer.cs` nor inline `[Migration("...")]` + `[DbContext(typeof(GatewayDbContext))]` attributes, EF never applies it. Existing databases keep working (they recorded it when it was still discoverable); a database created from scratch silently misses the change and fails later. Guard: `dotnet ef migrations list` must list every file in `src/Arkana.Infrastructure/Migrations`. |
| Non-streaming Codex returns upstream `400` for some accounts | Pre-existing upstream condition; verified identical on the previous release (parity check), unrelated to gateway releases. |
| Streamed Codex log rows have a blank `MODEL` | Logging gap for that provider path; the response itself is correct. |
| Qdrant healthcheck uses a bash TCP probe | The image has no `curl`/`wget`; intentional. |
| Slots carry no container healthcheck | The broker image has no HTTP client either; liveness is observed through the gateway's calls and `docker logs`. |

---

## Where things live

| Thing | Location |
|---|---|
| Compose + `.env` (per environment) | deploy host, `arkana-deploy/` (git-ignored `.env`) |
| Broker slots | `arkana-deploy/gemini-slots/` |
| Release approvals / manifests | `arkana-deploy/release-approvals/`, `~/live-release-<timestamp>/` |
| Rollback images | local registry tags `gateway:rollback-pre-<sha>-<timestamp>` |
| Deploy backups | `~/live-<feature>-<timestamp>/` per change, containing pre-change compose/.env copies |
