# deploy/infra — OS-level config under version control

The gateway *compose* stack lives in `../docker-compose.yml` and is fully
reproducible from the repo. The **host OS config** (nginx reverse proxy +
sslh port multiplexer) is also tracked here, captured from gateway-host on
2026-08-18, so a clean redeploy does not "kumat" (regress) the login/TLS fix.

## What belongs here

```
infra/
  setup-host.sh                # idempotent: applies the tracked files below
  nginx/arkana-gateway.conf      # live site from /etc/nginx/sites-enabled/ (captured)
  nginx/nginx.conf.grep.txt    # reference grep of main nginx.conf (context only)
  sslh/default                 # live /etc/default/sslh (captured)
  README.md                    # this file
  04-provision-openrouter.sql  # DB catalog provisioning (OpenRouter default model)
```

## Status

| File              | Source on server                 | In repo? | Applied by                |
|-------------------|----------------------------------|----------|---------------------------|
| nginx site conf   | `/etc/nginx/sites-enabled/arkana-gateway.conf` | ✅ captured | `setup-host.sh` -> `sites-enabled/` |
| sslh default      | `/etc/default/sslh`              | ✅ captured | `setup-host.sh` -> `/etc/default/sslh` |

`nginx/arkana-gateway.conf` terminates TLS on `:8443` (domain `gateway.arkana.dev`)
and proxies `/` -> gateway `127.0.0.1:5011`, `/n8n` -> n8n `127.0.0.1:5678`.
sslh demuxes `:443` -> nginx `:8443` (TLS) and `:1212` (SSH). The prior `9444`
was patched to `8443` to match nginx's listen port.

> NOTE: TLS certs in `/etc/nginx/ssl/gateway.arkana.dev/` are NOT tracked
> (secrets). Back them up out of band; `setup-host.sh` does not touch them.

## Applying (on gateway-host, as root)

```bash
cd /path/to/gateway-repo
bash deploy/infra/setup-host.sh
```

The script copies the tracked files into place, runs `nginx -t`, and reloads
`nginx` + restarts `sslh`. Idempotent; refuses to reload if `nginx -t` fails.

## DB provisioning (separate from OS config)

OpenRouter needs a catalog row or the default model 404s to the dead OpenCode
upstream. See `04-provision-openrouter.sql` and run it per its header.
