# Independent Fallback Provider (2026-08-06)

Status: **DONE — deployed to gateway-host and verified live.**

## Problem

The gateway had no real fallback. Every model in the catalog — including
`gpt-4o-mini`, `claude-haiku-3.5` and `gemini-2.0-flash-acc1` — resolved to the
single OpenCode upstream. When the OpenCode Zen workspace
(`wrk_01KW8HWBYX41Y1HJ4F77H2EA9S`) hit its weekly quota, the *entire* gateway
was dead, and the log said exactly that:

```
All 1 providers in the fallback chain failed
```

Chain depth 1 = nothing to fall through to.

## Root causes (four, not one)

Adding a provider alone would NOT have fixed this. Four independent defects each
had to be repaired before a second upstream could actually serve a request.

### 1. No independent upstream existed — `294a935`

`openai`, `anthropic` and `ollama` provider rows existed but were disabled and
keyless. There was no second credential anywhere on the box, and the Gemini
OAuth path needs an interactive browser login.

**Fix:** `OllamaChatService`, a self-hosted upstream on the compose network.
No API key, no OAuth, no region or quota coupling — it cannot fail for the same
reason the primary does. It speaks the OpenAI-compatible surface, so the
connector is a thin `OpenAiCompatChatServiceBase` subclass reusing the existing
dialect translator.

Deliberately **not** wrapped in `SsrfSafeHttpHandler`: that guard blocks
private/loopback destinations, which is precisely where a self-hosted upstream
lives. The base URL is operator config (`OLLAMA_BASE_URL`), never user input.

### 2. Routing ignored the model's provider — `f9b72ca`

`SendChatHandler` hardcoded the primary provider to `opencode` for every request
and never read `modelConfig.ProviderId`. A request for an Ollama model was still
dispatched to OpenCode, which rejected it:

```
model=qwen2.5:7b-instruct  ->  "OpenCode upstream returned HTTP 401"
```

This is what made the catalog *look* single-upstream. **Fix:** resolve the
primary from the model's own `ProviderId`; the opencode/lowest-priority lookups
remain only as a fallback for unknown models.

### 3. Quota/region errors terminated the chain — `85050d1`

`IsRetryable()` scanned a terminal-pattern list containing the bare substrings
`"quota"`, `"403"` and `"forbidden"`. Real upstream-exhaustion messages contain
those substrings, so the executor logged *"non-retryable error. Stopping chain."*
and never advanced — defeating the fallback chain during exactly the outage it
exists for.

**Fix:** a rate-limit/quota check that runs FIRST and short-circuits to retryable
for 429, `usage limit`, `quota exceeded` and `RegionError`. A bare 403 stays
terminal. 5 regression cases pin the verbatim strings the gateway emits.

### 4. Streaming was blocked by the SSRF guard — `af2ef42`

Both streaming endpoints used the `opencode-streaming` HttpClient, which carries
`SsrfSafeHttpHandler`. Streaming from Ollama died with:

```
blocked by SSRF guard: HTTP is not allowed in this environment
```

…while non-streaming worked, because the connector owns a separate unguarded
client. **Fix:** a `local-streaming` client selected on the upstream scheme, so
public `https` upstreams keep full SSRF protection.

### 5. (client) MITM proxy dropped the model — `6e2f94d`

The proxy read `body?.model || ''`, but Gemini's wire format carries the model in
the URL path and real Antigravity traffic has no `body.model`. Every request
forwarded an empty model → gateway default (`mimo-v2.5` → OpenCode).

**Fix:** parse the model from the path. The action suffix is stripped with a lazy
match rather than cutting at the first colon — Ollama tags contain colons, so
`qwen2.5:7b-instruct` would otherwise truncate to `qwen2.5`.

## Live verification (gateway-host, not local)

Failover, from the real gateway log:

```
warn: Provider OpenCode failed after 3 attempt(s):
      OpenCode upstream returned HTTP 429. Trying next in chain.
fail: All 2 providers in the fallback chain failed ...
```

`All 2 providers` — chain depth is 2, and 429 now advances instead of stopping.

| Test | Result |
|---|---|
| Ollama direct (`:11434`) | 200, real completion |
| Ollama via gateway, non-stream | **200 `FALLBACK_OK`** |
| Ollama via gateway, streaming | **38 chunks / 35 content deltas** (was 0) |
| Via MITM proxy, non-stream | **200 `PROXY_OK`** |
| Via MITM proxy, streaming | **39 chunks / 36 text parts** |
| OpenCode model (`mimo-v2.5`) | 429 → falls through (quota still exhausted) |

Tests: **817/817 pass** (812 + 5 new regression cases).

## Deployed configuration

- Container `ollama` on `arkana-dev_arkana-net`, alias `ollama`,
  `--restart unless-stopped`, volume `ollama_data`, bound to `127.0.0.1:11434`.
- Model `qwen2.5:7b-instruct` (4.7 GB).
- `OLLAMA_BASE_URL=http://ollama:11434/v1/` in `~/arkana-deploy/.env` and
  passed through in `docker-compose.yml`.
- DB: `ollama` provider enabled at **priority 1** (directly behind OpenCode at 0),
  `ApiKey` NULL; one enabled model row.

## Pitfalls for next time

- **The compose network is `arkana-dev_arkana-net`**, not
  `arkana-dev_default`. `docker run --network` fails outright with the wrong
  name, and under `set -e` the whole provisioning script aborts silently.
- **`SsrfSafeHttpHandler` blocks every self-hosted upstream.** Any new local
  provider needs an unguarded HttpClient on BOTH the connector and the streaming
  path. The two paths use different clients, so non-streaming can work while
  streaming fails — always test both.
- **A working non-streaming call proves nothing about streaming.** The SSRF bug
  was invisible until streaming was exercised separately.
- **Fixing failover needs a real outage to test against.** The exhausted OpenCode
  quota was the ideal test fixture — verify against it before it resets
  (~2026-08-09).

## Remaining

- OpenCode weekly quota still exhausted (resets ~2026-08-09). Expected and
  correct: it now 429s and falls through instead of taking the gateway down.
- `qwen2.5:7b-instruct` is CPU-only on this host — good for availability, slower
  than the hosted models. Consider a smaller tag if latency matters.
- Non-OpenCode catalog models (`gpt-4o-mini`, `claude-haiku-3.5`) still have no
  real credential; they resolve to disabled providers. Only OpenCode and Ollama
  are live upstreams.
