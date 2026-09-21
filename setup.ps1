#!/usr/bin/env pwsh
# ── ARKANA GATEWAY — Interactive Setup Wizard (Windows) ──
param()

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "`n=== ARKANA GATEWAY — Setup Wizard ===`n" -ForegroundColor Cyan

# Check dotnet
try {
    $v = dotnet --version
    Write-Host "  [✓] .NET SDK $v" -ForegroundColor Green
} catch {
    Write-Host "ERROR: .NET SDK 10.0+ required." -ForegroundColor Red
    exit 1
}

dotnet run --project "$scriptDir\tools\Arkana.Setup"
