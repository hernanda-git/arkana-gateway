# Portal Routing Configurability (2026-08-06)

Status: **DONE — deployed to gateway-host and verified live.**

Follow-up to `fallback-provider-2026-08-06.md`. That work gave the gateway a
second real upstream; this work makes routing to it *configurable* and stops
the model catalog from lying to agents.

## Question that started this

> Can routing be configured per client API key from the portal, can the default
> be changed, and is `/models` available to all agents?

Answer at the time: partly. Investigating each part turned up four defects.

## 1. `/v1/models` advertised dead models, to anyone — `b306707`

The handler was `modelRepo.GetAllAsync()` with **no filtering at all**:

```
32 models: 23 OpenCode (quota exhausted) · 4 OpenAI · 3 Anthropic · 1 User · 1 Ollama
```

The 7 OpenAI/Anthropic models belong to **disabled, credential-less providers**
— they could never serve a request. Agents populate their model pickers from
this catalog, so every agent offered the user a list that was mostly dead.

Two further problems:

- **No auth.** `/v1/models` was excluded from the auth middleware in
  `Program.cs`. Verified: `curl` with no key returned **HTTP 200** and the full
  catalog. Model codes and provider names leaked to anyone who could reach the
  host.
- **No per-key scoping.** A key restricted via `AllowedModels` still *saw* all
  32 and only discovered the restriction as a 403 at call time. It could not
  have been scoped anyway, because no key was resolved on an anonymous route.

**Fix:** authenticate the endpoint, filter to `m.IsEnabled && m.Provider.IsEnabled`,
and intersect with the caller's allow-list from `ApiKeyAuthMiddleware`. An empty
allow-list still means "all models", per `ApiKey.CanAccessModel`.

Live: anonymous → **401**; authenticated → **24 models**, the 7 dead ones gone.

## 2. Default model was hardcoded in 7 places — `b306707`, `f45ed3c`

The literal `"mimo-v2.5"` appeared 7 times across `ChatEndpoints` and
`ResponsesEndpoints`. That model belongs to OpenCode, so while the OpenCode
quota was exhausted **every request that omitted a model hit a dead upstream**
even though a healthy provider was available — and repointing it required a
recompile.

Centralised in `GatewayDefaults`, read from the environment:

| Variable | Default | Purpose |
|---|---|---|
| `GATEWAY_DEFAULT_MODEL` | `mimo-v2.5` | Model when the caller omits one |
| `OPENCODE_FORCE_SANDBOX_MODEL` | on | Set `0` to stop rewriting opencode-bound models |
| `OPENCODE_SANDBOX_MODEL` | `mimo-v2.5` | The model the sandbox key authorizes |

`OPENCODE_FORCE_SANDBOX_MODEL` deserves note: the Responses API silently
rewrote *every* opencode-bound model to `mimo-v2.5`. That is a workaround for a
one-model sandbox key, not routing — it overrides the caller's explicit choice
and becomes actively wrong once the key permits more. It is now opt-out.

**A second defect hid behind the first.** After deploying with
`GATEWAY_DEFAULT_MODEL=qwen2.5:7b-instruct` correctly present in the container,
a model-less request *still* returned OpenCode 401. The non-streaming path
defaulted `SendChatCommand.Model` to the literal **`"default"`** — not a real
model code, matching no row, so routing fell through to opencode. Fixed in
`f45ed3c`. This is why deploy-then-verify matters: the env var was right and the
behaviour was still wrong.

Live: request with no `model` field → **200, served by `qwen2.5:7b-instruct`**.

## 3. Provider routing was not configurable per client — `b306707`

The only way to pin a client to an upstream was to restrict its model list,
which conflates *what may this client use* with *where should it run*.

Added `ApiKey.PreferredProviderCode` (varchar 64, nullable), surfaced as a
**Route To Provider** dropdown in the portal's API-key edit modal, carried
through `ApiKeyAuthMiddleware` → `SendChatCommand.PreferredProvider`. The
fallback chain already honoured that field as the chain primary, so no executor
change was needed.

**Live proof** — a key pinned to `ollama` requesting `mimo-v2.5`, a model owned
by *OpenCode*:

```
warn: Provider Ollama failed after 3 attempt(s):
      Ollama upstream returned HTTP 404. Trying next in chain.
      Provider OpenCode failed: OpenCode upstream returned HTTP 429
```

The chain order is **inverted** — Ollama first, OpenCode second. Without the pin
OpenCode is always primary. The 404 is correct: Ollama does not host
`mimo-v2.5`. Same key with a model Ollama does host → **200 `PINNED_OK`**.

## Deployed configuration

```
GATEWAY_DEFAULT_MODEL=qwen2.5:7b-instruct   # ~/arkana-deploy/.env
```

Passed through in `docker-compose.yml` as
`GATEWAY_DEFAULT_MODEL: ${GATEWAY_DEFAULT_MODEL:-mimo-v2.5}`.

Schema change applied by hand — the app uses `EnsureCreated`, which does **not**
add columns to an existing table:

```sql
ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "PreferredProviderCode" varchar(64);
```

Tests: **819/819** (817 + 2 regression tests for `/v1/models` filtering).

## Answers to the original question

| Ask | Status |
|---|---|
| Per-key routing to another provider | **Yes** — portal → API key → *Route To Provider* |
| Per-key model restriction | Yes, already existed (empty = all models) |
| Change the default | **Yes** — `GATEWAY_DEFAULT_MODEL` in `.env`, no rebuild |
| Provider priority / enable-disable | Yes, already existed — portal → Providers |
| `/models` for all agents | **Yes** — OpenAI-compatible, now authenticated and scoped |

## Pitfalls

- **`EnsureCreated` never migrates.** New entity properties need a manual
  `ALTER TABLE` on the live DB or the query fails on a missing column.
- **A correct env var does not mean correct behaviour.** `GATEWAY_DEFAULT_MODEL`
  was present in the container while a separate hardcoded `"default"` string
  silently overrode it. Verify the observable outcome, not the config.
- **Provider-pinning a key does not bypass the fallback chain** — it only sets
  the *primary*. A pinned key still falls through to other providers, which is
  usually what you want, but it does mean a pinned client can be served by a
  different upstream than the pin names.
- When testing a pin, request a model the pinned provider **actually hosts**;
  otherwise the correct result is a 404 from that provider, which looks like a
  failure but is proof the pin worked.
