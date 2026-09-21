#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose="$root/deploy/docker-compose.gemini-broker.yml"
grep -Eq 'GEMINI_BROKER_IMAGE_REPOSITORY.*GEMINI_BROKER_IMAGE_DIGEST' "$compose"
grep -Fq 'GEMINI_BROKER_IMAGE_DIGEST' "$compose"
[ "$(grep -c '^  gemini-broker-[ab]:$' "$compose")" = 2 ]
[ "$(grep -c '^  gemini_slot_[ab]_auth:' "$compose")" = 2 ]
[ "$(grep -c '^  gemini_slot_[ab]_config:' "$compose")" = 2 ]
! grep -Eq '^    ports:' "$compose"
for script in "$root"/scripts/gemini-broker-{health,backup,restore}.sh; do
  grep -Eq 'set -euo pipefail' "$script"
  ! grep -Eq '(^|[[:space:]])(env|printenv|set)[[:space:]]' "$script"
done
for config in "$root"/deploy/gemini-broker/config/slot-{a,b}.yaml; do
  grep -Fq 'max-retry-credentials: 1' "$config"
  grep -Fq 'session-affinity: false' "$config"
  grep -Fq 'SET_OUT_OF_BAND' "$config"
done
printf 'PASS: Gemini broker static safety checks\n'
