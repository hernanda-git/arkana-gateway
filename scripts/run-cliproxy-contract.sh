#!/usr/bin/env bash
set -euo pipefail

: "${CLIPROXY_CONTRACT_SLOT_A_URL:?Set slot A data/management URL}"
: "${CLIPROXY_CONTRACT_SLOT_B_URL:?Set slot B data/management URL}"
: "${CLIPROXY_CONTRACT_MANAGEMENT_KEY_A:?Set slot A management key}"
: "${CLIPROXY_CONTRACT_MANAGEMENT_KEY_B:?Set slot B management key}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
dotnet test tests/Arkana.Infrastructure.Tests -c Release --nologo --filter 'FullyQualifiedName~CLIProxyApiManagementContractTests'
