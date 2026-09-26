# Networking and reverse proxies

Out of the box Cinomni listens on `127.0.0.1:8080`: plain HTTP, reachable only from the server
itself. That is deliberate. The container has no TLS and none of the protection a public endpoint
needs, so it is up to you how it is reached from elsewhere.

**Always create the administrator account before Cinomni is reachable from another machine.** The
setup page gives the administrator role to whoever uses it first.

## Choose how people reach it

| Option | Good for | What to do |
|---|---|---|
| **Only on the server** | Trying it out | Nothing. Use an SSH tunnel from another machine: `ssh -L 8080:127.0.0.1:8080 server`. |
| **A private network or VPN** (WireGuard, a mesh VPN, your LAN) | Most households | Set `CINOMNI_BIND_ADDRESS` to the server's address on that network. No proxy needed, but traffic is plain HTTP, so only do this on a network you trust. |
| **The internet, behind a reverse proxy** | Watching away from home without a VPN | Keep `CINOMNI_BIND_ADDRESS=127.0.0.1` and put a reverse proxy with TLS in front, as below. |

Serve Cinomni at the root of its own host name (for example `https://media.example.com/`).

## Requirements for a reverse proxy

A proxy in front of Cinomni must do all of these:

1. **Terminate TLS** and redirect plain HTTP to HTTPS. Passwords and session tokens travel on every
   request.
2. **Set `X-Forwarded-For` itself**, replacing whatever the client sent, and **declare the proxy** to
   Cinomni (next section). Otherwise sign-in rate limiting either locks the whole household out
   together or can be bypassed.
3. **Keep query strings out of the access log.** The web player passes the session token in the URL
   of video and subtitle requests, because media elements cannot send headers. A log of full URLs is a
   log of tokens.
4. **Forward the scheme** (`X-Forwarded-Proto`), and then set `CINOMNI_HSTS=true` so browsers stick to
   HTTPS. Set it only when TLS really is in front: browsers remember it for six months.
5. **Not buffer responses**, and allow long-lived connections: live updates use a server-sent event
   stream (`/api/realtime/stream`), and video is streamed.

## Tell Cinomni about the proxy

In `.env`:

```bash
CINOMNI_TRUSTED_PROXIES=172.18.0.0/16   # the address or range the proxy connects from
CINOMNI_PROXY_HOPS=1                     # only if several proxies are chained: how many
CINOMNI_HSTS=true
```

With a proxy running on the host, the connection reaches the container from the Docker network's
gateway, not from `127.0.0.1`, so the range of the Docker network is usually the right value. At
startup Cinomni logs which client address it counts and how many proxies it trusts; check that line
after the change. A proxy that is not listed is simply not believed, which fails safe.

## Example: Caddy

Caddy obtains certificates, redirects to HTTPS, sets `X-Forwarded-For` and `X-Forwarded-Proto`
correctly and streams responses, all by default:

```caddyfile
media.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

If you turn on Caddy's access log, keep query strings out of it.

## Example: nginx

nginx needs to be told more. In particular, a bare `proxy_pass` relays the client's own
`X-Forwarded-For`, which lets anyone choose their rate-limit bucket.

```nginx
# Log the path without the query string (which can carry a session token).
log_format cinomni '$remote_addr [$time_local] "$request_method $uri $server_protocol" '
                   '$status $body_bytes_sent';

server {
    listen 80;
    server_name media.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name media.example.com;

    ssl_certificate     /etc/letsencrypt/live/media.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/media.example.com/privkey.pem;

    access_log /var/log/nginx/cinomni.access.log cinomni;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;

        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $remote_addr;   # set, never relay
        proxy_set_header X-Forwarded-Proto $scheme;

        proxy_buffering    off;     # live updates and video
        proxy_read_timeout 1h;
    }
}
```

## Health checks through a proxy

`/health`, `/health/live` and `/health/ready` need no sign-in and reveal only a status per check
(database, torrent engine, storage). Decide whether to expose them publicly; a monitoring system
usually only needs `/health/ready`, and can reach it on the server directly.

## More

- [DEPLOYMENT.md, "Publishing beyond loopback"](../../DEPLOYMENT.md#publishing-beyond-loopback)
- [SECURITY.md, "What an internet-facing deployment does not get from Cinomni"](../../SECURITY.md#what-an-internet-facing-deployment-does-not-get-from-cinomni)
