# deploy/infra/sslh — captured host sslh config (REAL, from gateway-host)

`default` is the live `/etc/default/sslh` captured from gateway-host on
2026-08-18. It is tracked so a clean redeploy does not regress the TLS/SSH
multiplexing that the login fix depends on.

Current `DAEMON_OPTS` (as captured):
```
-p 0.0.0.0:443 -p [::]:443 --tls 127.0.0.1:8443 --ssh 127.0.0.1:1212 --on-timeout ssh
```
`--tls 127.0.0.1:8443` routes HTTPS/TLS to nginx; `--ssh 127.0.0.1:1212`
routes SSH to the sshd (the port the gateway box listens on). The prior
`9444` was patched to `8443` to match nginx's listen port.

## Deploy (see `../setup-host.sh`)

Copies `default` -> `/etc/default/sslh`, restarts sslh.
