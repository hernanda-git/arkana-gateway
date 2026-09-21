#!/bin/sh
set -eu

: "${CLIPROXY_TEST_IMAGE:?CLIPROXY_TEST_IMAGE is required}"
: "${CLIPROXY_TEST_NETWORK:?CLIPROXY_TEST_NETWORK is required}"
: "${CLIPROXY_TEST_MANAGEMENT_KEY:?CLIPROXY_TEST_MANAGEMENT_KEY is required}"

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
container_name="cliproxy-clean-volume-test-$$"
volume_name="cliproxy-clean-volume-test-$$"
config_path=/etc/cliproxy/config.yaml
template_path=/usr/local/share/cliproxy/config.template.yaml

cleanup() {
    docker rm -f "$container_name" >/dev/null 2>&1 || true
    docker volume rm "$volume_name" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

docker volume create "$volume_name" >/dev/null
docker run -d \
    --name "$container_name" \
    --network "$CLIPROXY_TEST_NETWORK" \
    --mount "type=volume,source=$volume_name,destination=/etc/cliproxy" \
    --mount "type=bind,source=$script_dir/cliproxy-entrypoint.sh,destination=/usr/local/bin/cliproxy-entrypoint,readonly" \
    --mount "type=bind,source=$script_dir/config.template.yaml,destination=$template_path,readonly" \
    --env "CLIPROXY_CONFIG_PATH=$config_path" \
    --env "CLIPROXY_CONFIG_TEMPLATE=$template_path" \
    --env "CLIPROXY_MANAGEMENT_KEY=$CLIPROXY_TEST_MANAGEMENT_KEY" \
    --health-cmd "curl -fsS --max-time 3 http://127.0.0.1:8317/ >/dev/null" \
    --health-interval 2s \
    --health-timeout 3s \
    --health-retries 15 \
    --health-start-period 5s \
    --entrypoint /usr/local/bin/cliproxy-entrypoint \
    "$CLIPROXY_TEST_IMAGE" -config "$config_path" >/dev/null

attempt=1
while [ "$attempt" -le 30 ]; do
    health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container_name")
    case "$health" in
        healthy)
            docker exec "$container_name" test -s "$config_path"
            printf '%s\n' 'clean_volume_startup=ok'
            exit 0
            ;;
        unhealthy)
            printf '%s\n' 'clean_volume_startup=failed'
            exit 1
            ;;
    esac
    attempt=$((attempt + 1))
    sleep 2
done

printf '%s\n' 'clean_volume_startup=timeout'
exit 1
