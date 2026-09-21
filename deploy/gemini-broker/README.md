# Gemini subscription broker (CLIProxyAPI) — slots & accounts

Routes `gemini-subscription` accounts (Antigravity / Gemini on a Google subscription)
through a local broker instead of the public Gemini API. One **slot** = one broker
container = **one Google identity**.

```
gateway ──HTTP──▶ gemini-broker-<slot>:8317 ──OAuth token──▶ Google (Cloud Code / Antigravity)
   │                     │
   │                     └── /root/.cli-proxy-api/<credential>.json   (auth volume, per slot)
   └── GeminiBroker__Slots__gemini-broker-<slot>__{BaseUrl,ManagementKey,DataPlaneKey}
```

## Files in this directory

| File | Purpose |
|---|---|
| `add-slot.sh` | Generates a slot: key in `<deploy-dir>/.env`, container in `gemini-slots/docker-compose.gemini-slots.yml`, gateway wiring with `--wire`, and a post-wire assertion of the gateway env. |
| `cliproxy-entrypoint.sh` | Slot entrypoint: renders `config.yaml` from the template and the injected key. |
| `config.template.yaml` | Per-slot config template (management plane, `api-keys`, retry/affinity settings). |
| `docker-compose.gemini-broker.yml` | Opt-in overlay with two example slots; no host `ports:`, `expose: 8317` only. |
| `antigravity-https-callback.patch` + `Dockerfile` | The patched broker build whose OAuth redirect is the public callback URL instead of `localhost:51121`. Slots must run this image (`cliproxy-api-antigravity:<tag>`), otherwise the Google consent page rejects the redirect. |
| `verify-clean-volume.sh` | Preflight that proves a slot starts from an empty volume. |

Non-secret examples only. `secret-key: SET_OUT_OF_BAND` is a sentinel: the real value
lives in the slot's private config volume / `<deploy-dir>/.env` and must never be
committed or printed.

## Create a slot and attach an account

```bash
cd <deploy-dir>/gemini-slots          # add-slot.sh + entrypoint + template live here
./add-slot.sh c --wire                # several at once: ./add-slot.sh d e f --wire
```

Per slot the generator:

1. writes `GEMINI_BROKER_<SLOT>_MANAGEMENT_KEY` into `<deploy-dir>/.env` (only once);
2. renders and starts `gemini-broker-<slot>` (own auth + config volumes, `expose: 8317`,
   no host ports);
3. with `--wire`, adds to the gateway compose

   ```yaml
   GeminiBroker__Slots__gemini-broker-<slot>__BaseUrl: http://gemini-broker-<slot>:8317
   GeminiBroker__Slots__gemini-broker-<slot>__ManagementKey: ${GEMINI_BROKER_<SLOT>_MANAGEMENT_KEY}
   ```

   recreates the gateway, and then asserts that the recreated container carries every
   wired slot **and** `GeminiSubscription__ProviderId` (a missing value there produces
   the 503 in "Troubleshooting" below).

Then, in the dashboard: `/providers` → **Add Antigravity Account** → pick the slot →
**Connect** → finish the Google consent. The dialog lists every configured slot; an
account stays bound to the slot it was created in.

Both planes are authenticated: the gateway sends
`Authorization: Bearer <DataPlaneKey || ManagementKey>`, so adding the same value to the
slot's `api-keys` in `config.template.yaml` locks the data plane down. `--wire`
installs the single-secret default; set `__DataPlaneKey` explicitly to split the planes.

## Verification

```bash
# slot is up and its management API answers with the slot key
IP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' gemini-broker-c)
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $KEY" "http://$IP:8317/v0/management/auth-files"

# the slot holds a Google credential (email + project_id present)
docker exec gemini-broker-c sh -c 'ls -la /root/.cli-proxy-api/'

# through the gateway, with an ephemeral key pinned to that account
curl -s  .../v1/chat/completions -d '{"model":"<gemini model>",...,"stream":false}'
curl -sN .../v1/chat/completions -d '{"model":"<gemini model>",...,"stream":true}'   # expect data: [DONE]
```

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `503 Gemini subscription streaming failed.` on streaming, non-stream fine | `GeminiSubscription__ProviderId` is missing from the **gateway** env. The connector returns "routing is not configured" before dialing the broker. Re-add the compose line and recreate the gateway; the startup log warns under `GeminiBrokerConfig`. |
| `400 Gemini subscription upstream request failed (HTTP 400)`, broker logs `antigravity auth missing project_id: request failed with status 403` | The slot's credential has no `project_id`: its sign-in was interrupted before Antigravity onboarding. Redo the consent for that account, or seed the credential from a healthy slot holding the same Google account (see `docs/OPERATIONS.md`). |
| Broker logs `Cloud Code Private API has not been used in project <client-project>` | Same root cause as above, one step later: the token is used without an onboarded project, so Google evaluates the OAuth client's project. |
| Broker logs `oauth flow timed out` | The consent was not finished within the flow window. The token may still be saved but **incomplete** — redo it and let it complete. |
| `unknown provider for model ...` right after a slot restart | The slot answers `/v1/models` only after its startup model refresh finishes; calls that race the restart are rejected. Wait for the refresh line in `docker logs` before testing. |
| `400 Broker slot is not allowlisted.` | The account's slot is not in `GeminiBroker__Slots__*` (or its `BaseUrl` is not an absolute http(s) URL). The startup log warns about this too. |
| `401` through the gateway while a direct broker call works | The broker enforces `api-keys` but the gateway sends a different key: align `api-keys` in the slot config with the gateway's slot key. |
| Slot never shows a container healthcheck | The broker image ships no `curl`/`wget`; liveness is observed through the gateway's calls and `docker logs`. Do not add a curl-based healthcheck. |
| Re-running `add-slot.sh` for an existing slot | Idempotent: existing keys and auth volumes are left alone, so a signed-in slot keeps its Google identity. |

## Backup & rollback

Before changing a slot, copy the two things that are hard to rebuild — the auth volume
(Google identity) and the config volume — and keep the gateway compose/`.env`:

```bash
docker cp gemini-broker-c:/root/.cli-proxy-api ./backup-c-auth
docker cp gemini-broker-c:/root/.cli-proxy-api/../config.yaml ./backup-c-config.yaml   # if present
cp <deploy-dir>/docker-compose.yml <deploy-dir>/.env ./backup-<timestamp>/
```

Rolling back a wiring change is then: restore the compose/`.env`, recreate the gateway
(`docker compose up -d --no-build --no-deps gateway`), and remove the slot container.
