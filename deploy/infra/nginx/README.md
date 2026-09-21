# deploy/infra/nginx — captured host nginx config (REAL, from gateway-host)

These files are the **live** server config that fixes gateway login/TLS,
captured from gateway-host on 2026-08-18. They are tracked so a clean redeploy
does not regress.

## Files

- `arkana-gateway.conf` — the enabled site (`/etc/nginx/sites-enabled/arkana-gateway.conf`).
  TLS termination on `:8443`, domain `gateway.arkana.dev`, proxies:
  - `/`        -> gateway  `127.0.0.1:5011`
  - `/n8n`     -> n8n      `127.0.0.1:5678`
  sslh demuxes `:443` TLS -> nginx `:8443` (see `../sslh/default`).
  Certs live at `/etc/nginx/ssl/gateway.arkana.dev/` (NOT tracked — they are
  secrets; back them up out of band).
- `nginx.conf.grep.txt` — reference grep of the main `nginx.conf` (listen/ssl/
  proxy/server_name/include lines) for context only; the site file above is
  what gets deployed.

## Deploy (idempotent, see `../setup-host.sh`)

```bash
bash deploy/infra/setup-host.sh
```

Copies `arkana-gateway.conf` -> `/etc/nginx/sites-enabled/`, runs `nginx -t`,
reloads nginx.
