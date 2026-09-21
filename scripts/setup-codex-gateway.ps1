[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$GatewayBaseUrl = 'https://gateway.arkana.dev/v1',
    [string]$Model = 'gpt-5.5',
    [string]$ProviderId = 'arkana-gateway',
    [switch]$SkipCodexVersionCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Safe([string]$Message) { Write-Host $Message }
function Assert-Tool([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found in PATH. Install it and run this script again."
    }
}
function Backup-File([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmarkana'
        $backup = "$Path.arkana-backup-$stamp"
        Copy-Item -LiteralPath $Path -Destination $backup -Force
        Write-Safe "BACKUP_CREATED=$backup"
    }
}

if ($GatewayBaseUrl -notmatch '^https://[^/]+/v1$') {
    throw 'GatewayBaseUrl must be an HTTPS URL ending in /v1.'
}
Assert-Tool codex

if (-not $SkipCodexVersionCheck) {
    $version = (& codex --version 2>$null | Select-Object -First 1)
    Write-Safe "CODEX_VERSION=$version"
}

$keySecure = Read-Host 'Enter the Gateway API key (input is hidden)' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($keySecure)
try {
    $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
}
if ([string]::IsNullOrWhiteSpace($key)) { throw 'Gateway API key was empty.' }

# Store the key in the per-user environment without printing it.
[Environment]::SetEnvironmentVariable('GATEWAY_API_KEY', $key, 'User')
$env:GATEWAY_API_KEY = $key
Write-Safe 'GATEWAY_API_KEY=stored in User Environment (value suppressed)'

$codexHome = Join-Path $HOME '.codex'
New-Item -ItemType Directory -Path $codexHome -Force | Out-Null
$configPath = Join-Path $codexHome 'config.toml'
$catalogPath = Join-Path $codexHome 'model_catalog.json'
Backup-File $configPath
Backup-File $catalogPath

$catalog = @'
{
  "models": [
    {
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
      "truncation_policy": { "type": "auto", "mode": "tokens", "limit": 128000 },
      "supported_reasoning_levels": [
        { "level": "low", "effort": "low", "description": "Fast reasoning" },
        { "level": "medium", "effort": "medium", "description": "Balanced reasoning" },
        { "level": "high", "effort": "high", "description": "Deep reasoning" }
      ]
    }
  ]
}
'@
[IO.File]::WriteAllText($catalogPath, $catalog, [Text.UTF8Encoding]::new($false))

$existing = if (Test-Path -LiteralPath $configPath) { [IO.File]::ReadAllText($configPath) } else { '' }
# Remove a previously managed block and top-level managed keys, preserving unrelated config.
$existing = [regex]::Replace($existing, '(?ms)^# BEGIN Arkana AI GATEWAY MANAGED BLOCK\r?\n.*?^# END Arkana AI GATEWAY MANAGED BLOCK\r?\n?', '')
$existing = [regex]::Replace($existing, '(?m)^model\s*=.*\r?\n', '')
$existing = [regex]::Replace($existing, '(?m)^model_provider\s*=.*\r?\n', '')
$existing = [regex]::Replace($existing, '(?m)^model_catalog_json\s*=.*\r?\n', '')
$managed = @"
# BEGIN Arkana AI GATEWAY MANAGED BLOCK
model = \"$Model\"
model_provider = \"$ProviderId\"
model_catalog_json = \"$($catalogPath.Replace('\','\\'))\"

[model_providers.$ProviderId]
name = \"ARKANA GATEWAY\"
base_url = \"$GatewayBaseUrl\"
wire_api = \"responses\"
env_key = \"GATEWAY_API_KEY\"
# END Arkana AI GATEWAY MANAGED BLOCK

"@
[IO.File]::WriteAllText($configPath, $managed + $existing.TrimStart(), [Text.UTF8Encoding]::new($false))

Write-Safe "CODEX_CONFIG=$configPath"
Write-Safe "MODEL_CATALOG=$catalogPath"

# Verify without printing the key.
$models = Invoke-RestMethod -Uri "$GatewayBaseUrl/models" -Headers @{ Authorization = "Bearer $key" }
$ids = @($models.data | ForEach-Object { $_.id })
if ($ids -notcontains $Model) {
    throw "Gateway responded, but model $Model was not present in /v1/models."
}
Write-Safe "GATEWAY_MODELS=OK"
Write-Safe 'AUTH_VALUE=SUPPRESSED'
Write-Safe 'NEXT_STEP=Open a new terminal and run the artifact-producing Codex Tool test from docs/codex-gateway-installation.md'
