#!/usr/bin/env bash
# ── ARKANA GATEWAY — Interactive Setup Wizard ──
# Cross-platform installer: Ubuntu, Windows (via WSL), macOS, Docker
set -euo pipefail

cd "$(dirname "$0")"

echo ""
echo "=== ARKANA GATEWAY — Setup Wizard ==="
echo ""

# Check dotnet
if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK 10.0+ is required."
    echo "  Install: https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
fi

dotnet_version=$(dotnet --version | cut -d'.' -f1)
if [ "$dotnet_version" -lt 10 ]; then
    echo "ERROR: .NET 10.0+ required, found $(dotnet --version)"
    exit 1
fi

echo "  [✓] .NET SDK $(dotnet --version)"

# Run the setup wizard
dotnet run --project tools/Arkana.Setup
