# Deploying Instella Server

This guide covers running Instella Server in production: Docker Compose, TLS through a reverse proxy,
configuration, storage, backups, upgrades and health checks. For what the server is trusted with, and what it
is not trusted with, see [security-model.md](security-model.md).

## What runs

One container (`instella-server`) serving plain HTTP on port **8080**. It includes the admin UI (Blazor
Server, which needs WebSockets), the update API under `/api/v1`, and `/healthz`. TLS terminates at a reverse
proxy in front of it.

The container keeps state in two volumes:

| Volume | Contents |
|---|---|
| `/config` | `instella.db` (SQLite: packages, versions, API keys, admin users, settings, audit log); `keys/` (ASP.NET Core Data Protection keys); `setup-token` (first run only); optionally `appsettings.json` |
| `/packages` | Package content (one blob per unique file, named by SHA-256) and generated patches (`patches/…`). Not used if you switch storage to S3. |

The database refers to blobs by hash. The two volumes belong together: back them up together and restore
them together.

**Run exactly one server per database.** The server coordinates uploads, deletions and clean-up of the same
content with locks inside its process (the database is SQLite). Two containers over the same volumes, or
over the same S3 bucket with copies of the database, can lose files. Scale up the one container instead.

## Getting the image

### From ghcr.io (recommended)

Images are published by release tags only (`.github/workflows/release.yml`): each release, including a
pre-release such as `0.2.0-rc.1`, publishes `ghcr.io/djgosnell/instella-server:<version>`, and a release
without a suffix also moves `:latest`. `:edge` is a one-off build of an unreleased commit that a
maintainer starts by hand (`.github/workflows/server-image.yml`); it is replaced each time and may not
exist. The images are public and linux/amd64 only. Point the compose file at the registry in `.env`:

```sh
INSTELLA_IMAGE=ghcr.io/djgosnell/instella-server
VERSION=0.1.0        # or latest; edge only when a maintainer has built one to try
INSTELLA_PORT=8580
```

and deploy or update with `scripts/deploy.sh <tag> --pull`. It pulls the image, backs up `.env`, the
compose file, `config/` and `packages/`, restarts the container and waits for it to report healthy.
Pin a version for a server others depend on; `edge` is only for trying an unreleased build.

### Building it yourself

```powershell
./scripts/build.ps1                          # version from Directory.Build.props, or -Version 0.1.0
./scripts/build.ps1 -ImageName my-instella   # another local image name
```

This publishes the server, builds `instella-server:<version>` from `src/Instella.Server/Dockerfile`, tags
it `:latest` too for a version without a suffix (never for a pre-release such as `0.2.0-rc.1`), and saves
it to `dist/instella-server-<version>.tar`. On the target host, `scripts/deploy.sh <version> --load` loads
that file and deploys it (`docker load -i instella-server-0.1.0.tar` by hand).

### What `deploy.sh` backs up

The backup stops the container, then copies `.env`, the compose file, `config/` and `packages/`
(hard-linked when the backup is on the same file system, since blobs never change in place) into
`$BACKUP_DIR/instella-<version>-<timestamp>/`. `deploy.sh --restore <name>` puts all of them back. For
backups between deployments, use the [backup procedure](#backups) below.

## Docker Compose

Put the repository's `deploy/docker-compose.yml` (and `deploy/config/` beside it) in a deployment directory (for example `/opt/instella`), with a
`.env` next to it:

```sh
INSTELLA_IMAGE=ghcr.io/djgosnell/instella-server   # leave out to use a locally built image
VERSION=0.1.0
INSTELLA_PORT=8580
```

### Prepare the volumes

The container runs as the .NET image's non-root user `app`, **uid and gid 1654** (fixed; it does not change
between versions). Docker creates missing bind-mount directories owned by root, and the server then cannot
write its database. Create the directories and give them to the container user (`scripts/deploy.sh` does this
when run as root, and prints the command otherwise):

```sh
cd /opt/instella
mkdir -p config packages
sudo chown -R 1654:1654 config packages
sudo chmod 700 config
```

`chmod 700` matters. On Linux the Data Protection keys in `config/keys` are stored unencrypted, so the
directory permissions are what protects them. The server also tries to set `/config` and `/config/keys` to 0700
at startup, but it cannot change a directory it does not own (a bind mount created by another user, or
a Docker Desktop mount from a Windows host); it then logs a warning and carries on. Set it yourself so
the directory is never exposed.

Named volumes (`instella-config:/config`) avoid the ownership step, because Docker copies the image's
directory ownership into a new named volume. The rest of this guide uses the bind mounts from the shipped
compose file.

### Start and complete first-run setup

```sh
docker compose up -d
docker compose logs instella-server
```

On first start the server applies the database migrations. Because no admin account exists yet, it also
writes a random **setup token** to `/config/setup-token` and logs
`Setup is not complete. Setup token written to /config/setup-token`. The log gives the path, not the token.
Read the token from the config volume:

```sh
sudo cat config/setup-token
# or: docker exec instella-server cat /config/setup-token
```

Open `https://<your-host>/setup` through the reverse proxy (see below) and enter the token, an admin
username and a password of at least 8 characters. The token file is deleted once the admin exists. Step 2
chooses storage: keep **Local Storage** with path `/packages`, or configure S3 (see [Storage](#storage)).

The token makes sure that whoever first reaches a newly exposed server cannot make themselves admin without
access to its files. If you restart before completing setup, the existing token stays valid.

Complete setup through the TLS proxy, not directly on port 8580. Outside Development the admin cookie is
always marked `Secure`, and browsers do not keep a `Secure` cookie received over plain HTTP. Signing in over
plain HTTP therefore does not work.

After setup, create packages, API keys and publisher keys in the admin UI. Enable 2FA under Settings.

## Reverse proxy with TLS

The Docker image sets `Server__TlsTerminatedByProxy=true`, so the server issues no HTTPS redirect and no HSTS
header. The proxy handles TLS and should send HSTS. Whatever proxy you use, it must:

- forward to the container's port 8080 (or the published host port);
- set `X-Forwarded-For` to the client address and `X-Forwarded-Proto` to `https`, replacing any values the
  client sent. The server reads only these two headers and processes one hop (`ForwardLimit = 1`);
- pass the original `Host` header;
- support WebSocket upgrades (the admin UI needs them);
- allow request bodies up to `Upload:MaxFileBytes` (2 GiB by default). One file is uploaded per request.

### Trusting the proxy: `KnownProxies` / `KnownNetworks`

The server trusts forwarded headers only from peers listed in `ReverseProxy:KnownProxies` (IP addresses) or
`ReverseProxy:KnownNetworks` (CIDR ranges). If both are empty, the forwarded-headers middleware is not
registered at all. With empty lists, ASP.NET Core's middleware would trust *every* peer, so any client could
choose its own address.

- **Without `KnownProxies`**, every request appears to come from the proxy's address. Download rate limits
  (`Downloads:PermitsPerMinute` per address), login and upload backoff, IP bans and the audit log all apply
  to the proxy. One client's failed logins then throttle everyone, and banning the proxy's address bans
  everyone.
- **Listing a network you do not control** lets anyone on it send a forged `X-Forwarded-For` and pick the
  address they are rate-limited, banned and logged under. List the exact proxy address where possible. Use
  `KnownNetworks` only for a network that contains nothing but your proxy and Instella.

In compose, set them as environment variables (array indexes in the name):

```yaml
    environment:
      - ReverseProxy__KnownProxies__0=172.30.0.1
      # - ReverseProxy__KnownNetworks__0=172.30.0.0/24
```

To check the result, make one failed sign-in and look at **Settings → Security → Recent Security Events**.
The logged address should be your client's public address, not the proxy's.

### nginx (on the host)

The proxy runs on the host and the container port is published on loopback only. Traffic from the host to a
published port reaches the container from the Docker network's gateway address. Pin the compose network's
subnet so that address stays fixed:

```yaml
services:
  instella-server:
    # ... as shipped, but:
    ports:
      - "127.0.0.1:${INSTELLA_PORT:-8580}:8080"
    environment:
      - ReverseProxy__KnownProxies__0=172.30.0.1   # gateway of the network below

networks:
  default:
    ipam:
      config:
        - subnet: 172.30.0.0/24
```

Every local process that connects to `127.0.0.1:8580` arrives from the gateway address and is trusted. Keep
the port bound to loopback.

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name updates.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name updates.example.com;

    ssl_certificate     /etc/letsencrypt/live/updates.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/updates.example.com/privkey.pem;
    add_header Strict-Transport-Security "max-age=31536000" always;

    client_max_body_size 2g;        # >= Upload:MaxFileBytes
    proxy_request_buffering off;    # stream uploads instead of spooling them to disk

    location / {
        proxy_pass http://127.0.0.1:8580;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $remote_addr;   # replace, do not append
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade           $http_upgrade;
        proxy_set_header Connection        $connection_upgrade;
        proxy_read_timeout 300s;
    }
}
```

### Caddy (on the host)

Caddy obtains certificates itself, handles WebSockets and sets `X-Forwarded-For`/`-Proto` automatically. It
replaces client-supplied values unless you configure `trusted_proxies`. It has no default body size limit.
Use the same compose changes as for nginx (loopback port, pinned subnet, `KnownProxies__0` = gateway).

```caddyfile
updates.example.com {
    header Strict-Transport-Security "max-age=31536000"
    reverse_proxy 127.0.0.1:8580
}
```

If Caddy runs as a container instead, attach it and Instella to a dedicated network, use
`reverse_proxy instella-server:8080`, and trust that network as in the Traefik example.

### Traefik (Docker labels)

Traefik runs as a container and reaches Instella over a shared Docker network. The container then publishes
no port. Traefik's address on that network is assigned dynamically, so trust the network with
`KnownNetworks` and make sure the network contains nothing but Traefik and Instella. Create the network
once with a fixed subnet:

```sh
docker network create --subnet 172.31.0.0/24 proxy
```

```yaml
services:
  instella-server:
    image: instella-server:${VERSION:-latest}
    container_name: instella-server
    restart: unless-stopped
    volumes:
      - ./config:/config
      - ./packages:/packages
    environment:
      - ReverseProxy__KnownNetworks__0=172.31.0.0/24
    networks: [proxy]
    labels:
      - traefik.enable=true
      - traefik.docker.network=proxy
      - traefik.http.routers.instella.rule=Host(`updates.example.com`)
      - traefik.http.routers.instella.entrypoints=websecure
      - traefik.http.routers.instella.tls.certresolver=letsencrypt
      - traefik.http.routers.instella.middlewares=instella-hsts
      - traefik.http.middlewares.instella-hsts.headers.stsSeconds=31536000
      - traefik.http.services.instella.loadbalancer.server.port=8080
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 10s

networks:
  proxy:
    external: true
```

This assumes a Traefik instance with a `websecure` entrypoint, a `letsencrypt` certificate resolver and an
HTTP-to-HTTPS redirect on its `web` entrypoint. Traefik sets `X-Forwarded-For` and `X-Forwarded-Proto`,
drops client-supplied forwarded headers by default (unless `forwardedHeaders.trustedIPs` says otherwise),
passes WebSockets through, and does not limit body size by default.

## Configuration reference

Most settings live in the database and are edited in the admin UI: storage provider and S3 credentials
(Settings), packages and their download access mode, publisher keys (Packages), API keys, and IP bans
(Settings → Security). The settings below come from configuration.

**Where configuration is read from**, lowest precedence first:

1. `appsettings.json` built into the image;
2. environment variables without a prefix, e.g. `Downloads__PermitsPerMinute=1200` (`:` becomes `__`);
3. `{configDir}/appsettings.json`, i.e. `/config/appsettings.json`, if present (reloaded on change);
4. environment variables with the `INSTELLA_` prefix, e.g. `INSTELLA_Downloads__PermitsPerMinute=1200`.

The config directory is the first of: the `Instella:ConfigDir` setting, the `INSTELLA_CONFIG_DIR` variable
(the image sets `/config`), `/config` if it exists on Linux, the application directory. The database, the
Data Protection keys and the setup token live there.

| Setting | Default | Meaning |
|---|---|---|
| `Database:ConnectionString` | `Data Source={configDir}/instella.db` | SQLite connection string. |
| `ReverseProxy:KnownProxies` | empty | Proxy IP addresses whose `X-Forwarded-For`/`-Proto` are trusted. Env: `ReverseProxy__KnownProxies__0`, `__1`, … |
| `ReverseProxy:KnownNetworks` | empty | Same, as CIDR ranges. Env: `ReverseProxy__KnownNetworks__0`. |
| `Server:TlsTerminatedByProxy` | `false`; image: `true` | `true` disables the HTTPS redirect and HSTS, which the proxy then provides. With `false` outside Development, the server redirects to HTTPS and sends HSTS itself (configure Kestrel certificates and HTTPS URLs). |
| `Downloads:PermitsPerMinute` | `600` | Download API requests (check-update, release, download, patch, installer) per client address per minute. Excess requests get 429. |
| `Api:PermitsPerMinute` | `300` | Package API requests (`packages`, `packages/{id}`, `…/versions`) per client address per minute. |
| `Auth:PermitsPerMinute` | `20` | Sign-in requests (login, TOTP, logout) per client address per minute, on top of the login backoff. |
| `Retention:SecurityEventDays` | `90` | Security log entries older than this are deleted by the hourly sweep. Repeats of an identical event (same type, address and package) within a minute are counted, not written; the next entry says "(+N similar)". Release decisions and publisher key changes are always written in full. A rejected release is deleted, so its log entry is its only record: raise this if you need a longer audit trail. |
| `Retention:DownloadLogDays` | `365` | Download log entries older than this are deleted by the hourly sweep. |
| `Upload:MaxFileBytes` | `2147483648` (2 GiB) | Largest single uploaded file. |
| `Upload:MaxFilesPerSession` | `20000` | Most files in one build. |
| `Upload:MaxPathLength` | `260` | Longest file path in a build, in characters. |
| `Upload:MaxPathSegments` | `32` | Deepest file path in a build. |
| `Diff:Enabled` | `true` | Generate binary patches between consecutive builds. |
| `Diff:MaxPatchRatio` | `0.9` | Discard a patch whose size is at least this fraction of the full build. |
| `Diff:MaxFileBytes` | `268435456` (256 MiB) | Files larger than this are not diffed; the patch carries them whole. Diffing needs several times the file size in memory, so lower it on a small server. A patch job that fails is retried with backoff. |
| `Storage:Local:BasePath` | `./packages`; image: `/packages` | Blob directory used only until setup saves storage settings. After that, the path saved in Settings wins. |
| `ASPNETCORE_URLS` | image: `http://+:8080` | Standard ASP.NET Core listen addresses. |
| `ASPNETCORE_ENVIRONMENT` | image: `Production` | Never run a public server as `Development`: it shows exception details and relaxes cookie security. |
| `Logging:LogLevel:*`, `AllowedHosts` | see `appsettings.json` | Standard ASP.NET Core settings. |

There are no S3 settings in configuration: S3 is configured in the admin UI (below).

The three rate limits apply per client address (after `KnownProxies` resolution); sign-in also backs off
per address after failed attempts. Requests to a private package without valid credentials get the same
answer as an unknown package, so package ids cannot be probed.

**Run one server instance per database.** Uploads, deletes and the sweeper coordinate through locks held
in the server process, and SQLite allows one writer. Two containers on the same `/config` and `/packages`
can lose blobs; scale up, not out.

## Storage

**Local** (default): blobs in `/packages`. **S3-compatible** (AWS S3, MinIO, Cloudflare R2, Backblaze B2,
DigitalOcean Spaces, custom): choose it in setup step 2 or later in **Settings → Storage Configuration**.
Use **Test Connection** before saving. The change applies without a restart.

- The endpoint must be `https`. "Allow insecure endpoint" exists only for a local MinIO during development:
  clients follow redirects to presigned URLs, and .NET refuses a redirect from https to http.
- Single-file and patch downloads redirect clients to presigned URLs (lifetime `S3UrlExpiryMinutes`,
  default 60). Full-build ZIPs are streamed through the server.
- The S3 secret key is encrypted in the database with the Data Protection keys (see [Backups](#backups)).
- Switching providers does not copy existing blobs. Switch before you upload, or copy the objects yourself
  (same keys) before saving the new settings.
- **Give Instella a dedicated bucket or directory.** Every hour (and at startup) the orphan sweeper lists the
  whole storage root and deletes objects older than 24 hours that no database row refers to. It only
  touches keys in Instella's own layout (`ab/cd/<sha256>`, `patches/.../patch.zip` and their temporary
  files), so other files survive, but an unrelated file that happens to match that layout would not. The
  local folder must be an absolute path, not a filesystem root, and must not be or contain the config
  directory; Settings refuses such a path.

## Backups

A complete backup has three parts: the **database**, the **Data Protection keys**, and the **blobs**.

### Order: database first, then blobs

Take the database snapshot first, then copy the blobs.

- A blob copied *after* the snapshot but not referenced by it (content uploaded in between) is harmless. The
  orphan sweeper deletes blobs with no database row once their file time is more than 24 hours old.
  Uploads that were half finished in the snapshot are cleaned up the same way: their content rows are
  removed 24 hours after upload.
- The reverse order is unsafe: a database newer than its blobs refers to content that is not there, and
  nothing repairs that. A full-build download then fails with "Content for '…' is missing from storage".
- Blobs are deleted only when versions, builds or packages are deleted. Avoid deleting them while a backup
  runs, or the blob copy can miss content that the database snapshot still refers to.

### Procedure (bind-mount layout, run as root from the deployment directory)

```sh
set -e
BACKUP=/srv/backup/instella-$(date +%Y%m%d-%H%M%S)
mkdir -p "$BACKUP"

# 1. Database: an online, consistent copy. The server image has no sqlite3, so use a helper container.
docker run --rm -v "$PWD/config:/config" -v "$BACKUP:/backup" alpine:3 \
  sh -c 'apk add --no-cache sqlite >/dev/null && sqlite3 /config/instella.db ".backup /backup/instella.db"'

# 2. Data Protection keys (and the config file, if you use one)
cp -a config/keys "$BACKUP/keys"
[ -f config/appsettings.json ] && cp -a config/appsettings.json "$BACKUP/"

# 3. Blobs
rsync -a packages/ "$BACKUP/packages/"
```

With `sqlite3` installed on the host, step 1 can be
`sqlite3 config/instella.db ".backup '$BACKUP/instella.db'"`. Do not copy `instella.db` with `cp` while the
server runs. That can capture a half-written file and misses any `-wal` companion file.

With S3 storage, step 3 is a copy of the bucket (`rclone sync`, `aws s3 sync`, or bucket versioning or
replication), still taken after the database snapshot.

### The keys are part of the backup

`/config/keys` holds the keys that encrypt the admin sign-in cookies, antiforgery tokens, pending
2FA sign-ins, and two secrets stored in the database: the **S3 secret key** and the admins' **TOTP (2FA)
secrets**. If you restore a database without its keys, everyone must sign in again, which is harmless. The
server also can no longer decrypt those secrets: S3 storage stops working, the Settings page fails to load,
and 2FA sign-in fails. Recovering means clearing or replacing the encrypted values in the database by hand.
Keep the keys with the database backup, and protect them as secrets: on Linux they are plain XML files.

### Restore

```sh
docker compose down
cp "$BACKUP/instella.db" config/instella.db
rm -f config/instella.db-wal config/instella.db-shm     # stale WAL files must not be applied to the restored DB
rm -rf config/keys && cp -a "$BACKUP/keys" config/keys
rsync -a --delete "$BACKUP/packages/" packages/
chown -R 1654:1654 config packages
docker compose up -d
```

## Upgrades

With the registry image, an upgrade is `scripts/deploy.sh <tag> --pull`. By hand:

1. **Back up first** (above). Migrations change the database in place, and an older image cannot be
   expected to run against a database a newer one has migrated. Rolling back means restoring that backup
   and deploying the previous tag (a `<version>` tag names one release exactly).
2. Pull, build or load the new image (`docker compose pull`, `scripts/build.ps1`, `docker load`), then set
   `VERSION` in `.env`.
3. `docker compose up -d`. On start the server applies any pending EF Core migrations, then serves requests.
   Watch `docker compose logs -f instella-server` and wait for the health check to report healthy.

`scripts/deploy.sh` waits up to 90 seconds for the container to report healthy. If it does not, it prints the
last health-check output and the command to restore the backup it made before deploying; it does not roll
back by itself.

Every schema change ships as a migration, so a newer server upgrades an existing database in place at
startup. Before 1.0 that is the practice, not a promise: the [CHANGELOG](../CHANGELOG.md) says when a
release needs a fresh database instead.

The container runs as uid 1654 (the .NET image's `app` user). Volumes created for a different uid need a
one-time `sudo chown -R 1654:1654 config packages`; `scripts/deploy.sh` checks and says so.

## Health checks

`GET /healthz` is anonymous and is not rate limited. It returns `200 Healthy` when the database answers and
`503 Unhealthy` when it does not. It does not check blob storage or the background workers.

The image's `HEALTHCHECK` (and the compose file) run `wget -qO- http://localhost:8080/healthz` every 30
seconds. `wget` is used because the Alpine image has no `curl`.

```sh
docker inspect --format '{{.State.Health.Status}}' instella-server
```

Point external monitoring and load-balancer checks at `https://<your-host>/healthz`. Background patch
generation and orphan sweeping report failures only in the log. A patch job that failed three times is
marked dead, and clients then fall back to full downloads for that version.
