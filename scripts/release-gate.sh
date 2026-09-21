#!/usr/bin/env bash
set -euo pipefail

BASELINE_REF="${BASELINE_REF:-cd2d7a1}"
TEST_FLOOR="${TEST_FLOOR:-907}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

fail() { printf 'RELEASE_GATE_FAIL: %s\n' "$*" >&2; exit 1; }
pass() { printf 'RELEASE_GATE_PASS: %s\n' "$*"; }

# A candidate must be reproducible from a clean, isolated release lane.
[ -z "$(git status --porcelain)" ] || fail "worktree is dirty"
git cat-file -e "${BASELINE_REF}^{commit}" || fail "baseline ref not available: ${BASELINE_REF}"
git merge-base --is-ancestor "$BASELINE_REF" HEAD || fail "HEAD is not based on baseline ${BASELINE_REF}"
pass "clean worktree and baseline ${BASELINE_REF}"

# These are the two production invariants that previously caused the login regression.
grep -q 'AddScoped<IProviderConnectorFactory, ProviderConnectorFactory>()' \
  src/Arkana.Infrastructure/DependencyInjection.cs \
  || fail "ProviderConnectorFactory is not scoped"
! grep -q 'AddSingleton<IProviderConnectorFactory' \
  src/Arkana.Infrastructure/DependencyInjection.cs \
  || fail "ProviderConnectorFactory singleton registration detected"
grep -q 'protected override Task OnAfterRenderAsync(bool firstRender)' \
  src/Arkana.Gateway.Api/Components/Pages/Dashboard.razor \
  || fail "dashboard interactive hydration boundary missing"
pass "DI and dashboard lifecycle invariants"

# Build and suite are the promotion gate. Keep full output in CI logs.
dotnet build Arkana.slnx -c Release --nologo -v q
pass "Release build"

test_output="$(mktemp)"
trap 'rm -f "$test_output"' EXIT
dotnet test Arkana.slnx -c Release --no-build --nologo | tee "$test_output"
failed=0
while IFS= read -r token; do
    n="${token#Failed:}"
    n="${n// /}"
    n="${n//$'\r'/}"
    failed=$((failed + n))
done < <(grep -oE 'Failed:[[:space:]]+[0-9]+' "$test_output")
passed=0
while IFS= read -r token; do
    n="${token#Passed:}"
    n="${n// /}"
    n="${n//$'\r'/}"
    passed=$((passed + n))
done < <(grep -oE 'Passed:[[:space:]]+[0-9]+' "$test_output")
[ "$failed" = 0 ] || fail "test failures: $failed"
[ "$passed" -ge "$TEST_FLOOR" ] || fail "test floor $TEST_FLOOR not met: $passed"
pass "full suite: passed=$passed, failed=$failed"
