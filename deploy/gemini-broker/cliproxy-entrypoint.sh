#!/bin/sh
set -eu

config_path=${CLIPROXY_CONFIG_PATH:-/etc/cliproxy/config.yaml}
template_path=${CLIPROXY_CONFIG_TEMPLATE:-/usr/local/share/cliproxy/config.template.yaml}

if [ ! -s "$config_path" ]; then
    management_key=${CLIPROXY_MANAGEMENT_KEY:-}
    if [ -z "$management_key" ]; then
        printf '%s\n' 'CLIPROXY_MANAGEMENT_KEY is required when the broker config is absent.' >&2
        exit 78
    fi
    case "$management_key" in
        *[!A-Za-z0-9._~+=:/-]*)
            printf '%s\n' 'CLIPROXY_MANAGEMENT_KEY contains unsupported YAML characters; mount a reviewed config instead.' >&2
            exit 78
            ;;
    esac
    # Data-plane key: the broker's api-keys list. Defaults to the management key
    # (one secret per slot) and matches what the gateway sends for this slot.
    data_plane_key=${CLIPROXY_DATA_PLANE_KEY:-$management_key}
    case "$data_plane_key" in
        *[!A-Za-z0-9._~+=:/-]*)
            printf '%s\n' 'CLIPROXY_DATA_PLANE_KEY contains unsupported YAML characters; mount a reviewed config instead.' >&2
            exit 78
            ;;
    esac
    if [ ! -s "$template_path" ]; then
        printf '%s\n' 'CLIProxyAPI config template is missing.' >&2
        exit 78
    fi

    config_dir=${config_path%/*}
    if [ "$config_dir" = "$config_path" ]; then
        config_dir=.
    fi
    mkdir -p "$config_dir"
    sed -e "s|__CLIPROXY_MANAGEMENT_KEY__|$management_key|g" \
        -e "s|__CLIPROXY_DATA_PLANE_KEY__|$data_plane_key|g" "$template_path" > "$config_path.tmp"
    chmod 600 "$config_path.tmp"
    mv "$config_path.tmp" "$config_path"
fi

exec /CLIProxyAPI/CLIProxyAPI "$@"
