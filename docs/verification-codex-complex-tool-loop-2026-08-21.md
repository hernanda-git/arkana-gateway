# Production Verification Report: Codex Complex Tool Loop

> **Date:** 2026-08-21
> **Status:** verified with real Codex CLI Tool events and local artifacts

## Configuration used

| Field | Verified value |
|---|---|
| Agent | Codex CLI 0.149.0 |
| Codex Provider id | `arkana-gateway` |
| Model | `gpt-5.5` |
| Wire/API mode | Responses API |
| Gateway Base URL | `https://gateway.arkana.dev/v1` |
| Credential source | `GATEWAY_API_KEY` plus local Codex fallback configuration |
| Gateway API key label | `Hernanda Codex Gateway Production` |
| Upstream Provider | ChatGPT OAuth, selected by Gateway |
| API key value | intentionally omitted |

## Test prompt behavior

The Codex Agent was instructed to use real Tools and not describe hypothetical execution. It was asked to:

1. Create a temporary test directory.
2. Write valid JSON containing exactly three records and marker `COMPLEX_GATEWAY_MARKER`.
3. Read the JSON file.
4. Search for the marker using PowerShell `Select-String`.
5. Parse the JSON and count the records.
6. Write a report file.
7. Read the report file back.
8. Attempt Browser Tool usage and report availability honestly.

## Actual Tool events

The Codex JSON event stream contained real `command_execution` events with `status: completed`. The executed commands used Windows PowerShell and included:

- `New-Item` for directory creation.
- `Set-Content` for JSON and report writes.
- `Get-Content` for reads.
- `Select-String` for marker search.
- `ConvertFrom-Json` for parsing and record count.

The main Codex command exited with code `0`.

## Actual artifact results

Test directory:

```text
C:\Users\user\AppData\Local\Temp\codex-complex-gateway-test
```

Files created:

```text
C:\Users\user\AppData\Local\Temp\codex-complex-gateway-test\dataset.json
C:\Users\user\AppData\Local\Temp\codex-complex-gateway-test\report.txt
C:\Users\user\AppData\Local\Temp\codex-complex-gateway-test\codex-events.jsonl
```

Observed values:

```text
marker=COMPLEX_GATEWAY_MARKER
record_count=3
grep_verification=COMPLEX_GATEWAY_MARKER
```

The JSON records were `alpha`, `beta`, and `gamma` with ids 1, 2, and 3.

## Browser result

Codex reported that no Browser Tool was available in that CLI environment. No URL was opened and no page title was claimed. This is an honest limitation of the client Tool surface, not a Gateway failure.

## Gateway production log verification

After the test, production Docker logs showed repeated successful pairs of:

```text
POST /v1/responses
POST https://chatgpt.com/backend-api/codex/responses
```

The relevant RequestLogs records showed:

```text
Provider: ChatGPT
Model: gpt-5.5
ApiKeyName: Hernanda Codex Gateway Production
IsError: false
```

The two latest recorded message payload sizes were 11025 and 2511 bytes. No key value was extracted or recorded.

## Acceptance decision

| Gate | Result |
|---|---|
| Real Codex command execution event | PASS |
| File write | PASS |
| File read | PASS |
| JSON parse | PASS |
| Marker search | PASS |
| Record count equals 3 | PASS |
| Report write/read | PASS |
| Gateway `/v1/responses` request | PASS |
| Gateway Provider routing | PASS |
| Gateway Request `IsError` | PASS, false |
| Browser Tool | NOT AVAILABLE, honestly reported |

This report proves a real multi-step Codex Tool loop through the custom ARKANA GATEWAY. It does not claim Browser Tool support where the installed Codex environment did not expose one.
