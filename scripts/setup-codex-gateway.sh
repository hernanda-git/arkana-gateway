#!/usr/bin/env bash
set -Eeuo pipefail

GATEWAY_BASE_URL="${GATEWAY_BASE_URL:-https://gateway.arkana.dev/v1}"
MODEL="${MODEL:-gpt-5.5}"
PROVIDER_ID="arkana-gateway"
CODEX_HOME="${CODEX_HOME:-$HOME/.codex}"
CONFIG="$CODEX_HOME/config.toml"
CATALOG="$CODEX_HOME/model_catalog.json"

case "$GATEWAY_BASE_URL" in
  https://*/v1) ;;
  *) printf '%s\n' 'GATEWAY_BASE_URL must be an HTTPS URL ending in /v1.' >&2; exit 1 ;;
esac
command -v codex >/dev/null || { printf '%s\n' 'codex is required in PATH.' >&2; exit 1; }
: "${GATEWAY_API_KEY:?Set GATEWAY_API_KEY in the environment without printing it.}"
mkdir -p "$CODEX_HOME"

backup() {
  local path="$1"
  if [[ -f "$path" ]]; then
    cp -p "$path" "$path.arkana-backup-$(date +%Y%m%d-%H%M%S)"
  fi
}
backup "$CONFIG"
backup "$CATALOG"

cat > "$CATALOG" <<'JSON'
{
  "models": [{
    "slug": "gpt-5.5",
    "display_name": "GPT-5.5 (ARKANA GATEWAY)",
    "provider": "arkana-gateway",
    "name": "GPT-5.5 via ARKANA GATEWAY",
    "supported_tools": ["shell_command"],
    "experimental_supported_tools": [],
    "supports_parallel_tool_calls": true,
    "reasoning": true,
    "shell_type": "shell_command",
    "visibility": "list",
    "supported_in_api": true,
    "priority": 100,
    "base_instructions": "",
    "support_verbosity": true,
    "tool_mode": "direct",
    "truncation_policy": {"type":"auto","mode":"tokens","limit":128000},
    "supported_reasoning_levels": [
      {"level":"low","effort":"low","description":"Fast reasoning"},
      {"level":"medium","effort":"medium","description":"Balanced reasoning"},
      {"level":"high","effort":"high","description":"Deep reasoning"}
    ]
  }]
}
JSON

python3 - "$CONFIG" "$CATALOG" "$GATEWAY_BASE_URL" <<'PY'
from pathlib import Path
import re, sys
config = Path(sys.argv[1])
catalog = Path(sys.argv[2])
base = sys.argv[3]
existing = config.read_text(encoding='utf-8') if config.exists() else ''
existing = re.sub(r'(?ms)^# BEGIN Arkana AI GATEWAY MANAGED BLOCK\r?\n.*?^# END Arkana AI GATEWAY MANAGED BLOCK\r?\n?', '', existing)
existing = re.sub(r'(?m)^model\s*=.*\r?\n', '', existing)
existing = re.sub(r'(?m)^model_provider\s*=.*\r?\n', '', existing)
existing = re.sub(r'(?m)^model_catalog_json\s*=.*\r?\n', '', existing)
managed = f'''# BEGIN Arkana AI GATEWAY MANAGED BLOCK
model = "gpt-5.5"
model_provider = "arkana-gateway"
model_catalog_json = "{catalog.as_posix()}"

[model_providers.arkana-gateway]
name = "ARKANA GATEWAY"
base_url = "{base}"
wire_api = "responses"
env_key = "GATEWAY_API_KEY"
# END Arkana AI GATEWAY MANAGED BLOCK

'''
config.write_text(managed + existing.lstrip(), encoding='utf-8', newline='')
print(f'CODEX_CONFIG={config}')
print(f'MODEL_CATALOG={catalog}')
PY

python3 - "$CATALOG" <<'PY'
import json
from pathlib import Path
import sys
json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
PY

curl --fail --silent --show-error "$GATEWAY_BASE_URL/models" \
  -H "Authorization: Bearer $GATEWAY_API_KEY" \
  -o /dev/null
printf '%s\n' 'GATEWAY_MODELS=OK'
printf '%s\n' 'AUTH_VALUE=SUPPRESSED'
printf '%s\n' 'NEXT_STEP=Open a new terminal and run the artifact-producing Codex Tool test from docs/codex-gateway-installation.md'
