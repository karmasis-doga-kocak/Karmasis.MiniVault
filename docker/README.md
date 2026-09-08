# MiniVault - Docker image and compose

Files in this folder:

| File | Purpose |
| --- | --- |
| `Dockerfile` | Multi-stage build (`sdk:10.0` -> `aspnet:10.0`), publishes `Karmasis.MiniVault.Server`. |
| `nuget.docker.config` | NuGet sources used **inside** the build stage (see comments in the file). |
| `build-local.ps1` | Stages nupkgs from the local-nuget folder feed and builds the image for local dev. |
| `docker-compose.yml` | `minivault-init` (profile `init`) + `minivault` services. |
| `.env.example` | Template for the environment variables compose needs. |

The application is HTTPS-only (Kestrel `Tls:Url`, default `https://0.0.0.0:8200`) and
needs a PFX certificate plus a base64 master key before it will serve traffic.

## 1. Build the image

### Local dev (uses the local-nuget folder feed)

The repo restores `Karmasis.Cryptography` only from `..\..\local-nuget`
(see the root `nuget.config`), a path that does not exist inside a Docker build
context. `build-local.ps1` bridges that: it copies the `.nupkg` files from the local
feed into `<repo>/packages` (git-ignored, consumed via `docker/nuget.docker.config`),
runs `docker build`, then cleans `packages/` back out.

```powershell
.\docker\build-local.ps1
# or explicitly:
.\docker\build-local.ps1 -LocalFeed D:\Karmasis\local-nuget -ImageTag karmasis/minivault:dev
```

This produces `karmasis/minivault:dev`.

### CI (private feed, no local nupkgs baked into the image)

CI passes a different NuGet config as a build arg (see Task 5 for the file that
provides `NUGET_AUTH_TOKEN` / `VSS_NUGET_EXTERNAL_FEED_ENDPOINTS`):

```bash
docker build -f docker/Dockerfile --build-arg NUGET_CONFIG=nuget-dev.config -t karmasis/minivault:ci .
```

## 2. Generate a certificate

The server needs a PFX at the path given by `Tls__Certificate__Path`
(`/certs/minivault.pfx` in the compose file), protected by `Tls__Certificate__Password`.
On Linux the PFX is loaded with `X509KeyStorageFlags.DefaultKeySet`, so a
self-signed dev certificate works fine.

**PowerShell** (Windows host):

```powershell
# Not $pwd: that is PowerShell's built-in alias for the current directory, and assigning to it
# breaks Get-Location for the rest of the session.
$pfxPassword = ConvertTo-SecureString -String "change-me" -Force -AsPlainText
$cert = New-SelfSignedCertificate -DnsName "localhost" -CertStoreLocation "cert:\CurrentUser\My" -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(2)
New-Item -ItemType Directory -Force -Path .\docker\certs | Out-Null
Export-PfxCertificate -Cert $cert -FilePath .\docker\certs\minivault.pfx -Password $pfxPassword | Out-Null
Remove-Item "cert:\CurrentUser\My\$($cert.Thumbprint)"
```

**openssl** (one-liner, any host):

```bash
mkdir -p docker/certs
openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -nodes \
  -keyout /tmp/minivault-key.pem -out /tmp/minivault-cert.pem -subj "/CN=localhost" \
  && openssl pkcs12 -export -out docker/certs/minivault.pfx \
     -inkey /tmp/minivault-key.pem -in /tmp/minivault-cert.pem -passout pass:change-me
```

`docker/certs/` is mounted read-only at `/certs` by the `minivault` service; do not
commit real certificates or passwords (`docker/certs/` and `docker/.env` are
git-ignored). The container runs as a non-root user (uid 1654), so the PFX has to be
readable by that uid — give it to the uid rather than to everyone:

```bash
chown 1654:1654 docker/certs/minivault.pfx
chmod 640 docker/certs/minivault.pfx
```

(`chmod 644` also works, but the PFX password lives in `docker/.env` right next to the
file, so the pair is worth keeping off other local accounts.)

## 3. Initialize

Set `CONNECTIONSTRINGS__MINIVAULT` (in `docker/.env`, copied from `.env.example`) and
run the one-shot init job:

```powershell
docker compose -f docker/docker-compose.yml --env-file docker/.env --profile init run --rm minivault-init
```

`init` refuses to run twice against an already-initialized database. Its stdout
prints, in order:

1. The master key, **only** because `MasterKey__Provider=Environment` cannot store it
   itself: `Master key (set as MINIVAULT__MASTERKEY ...): <base64>`
2. The recovery material: a `Recovery key:` line, or (for `--recovery shamir`)
   `Share 1:` / `Share 2:` / `Share 3:` lines.

Copy the master key into `docker/.env` as `MINIVAULT__MASTERKEY`. Store the recovery
key/shares somewhere safe outside the container (a password manager, sealed
envelopes for the shares, etc.) - they are not retrievable again.

`docker compose run --rm` deletes the init container as it exits, so there is no
container log left to clear: the master key survives only in your terminal scrollback
and in whatever your shell or terminal multiplexer records. Clear that, and do not
paste the output into a ticket.

**`MINIVAULT__MASTERKEY` is an environment variable of the running `minivault`
container**, which means `docker inspect minivault` prints it in clear text, as does
reading `/proc/<pid>/environ` on the host. Anyone with access to the Docker socket
therefore has the master key. That is inherent to `MasterKey__Provider=Environment`
(there is no OS-level key store in a Linux container the way DPAPI is on Windows):
restrict the socket, and for anything beyond a single-tenant host prefer a real secret
store or the Windows DPAPI provider.

## 4. Run

```powershell
docker compose -f docker/docker-compose.yml --env-file docker/.env up -d minivault
```

## 5. Health check

The image's `HEALTHCHECK` and the compose `healthcheck` both call
`curl -kfsS https://localhost:8200/v1/health` from inside the container
(self-signed cert, hence `-k`). From the host:

```powershell
curl -k https://localhost:8200/v1/health
docker compose -f docker/docker-compose.yml ps   # shows health status
```

Expected: `{"status":"ok","initialized":true,"activeDataKeyVersion":<n>}`.

## 6. Logs

```powershell
docker compose -f docker/docker-compose.yml logs -f minivault
```

Look for `Now listening on: https://...` to confirm Kestrel bound the TLS endpoint.

## 7. Upgrade

```powershell
docker pull karmasis/minivault:dev   # or rebuild with build-local.ps1 / CI
docker compose -f docker/docker-compose.yml up -d minivault
```

`MINIVAULT__MASTERKEY` and the certificate volume are unaffected by an image swap.
**`rotate-dek` requires a restart** of the `minivault` service afterwards (the
`DataKeyRing` is loaded once at startup) - run the CLI command against the same
database, then `docker compose -f docker/docker-compose.yml restart minivault`.

## Notes

- SQL Server is intentionally not part of this compose file - point
  `ConnectionStrings__MiniVault` at whatever instance you already run (a host SQL
  Server reachable via `host.docker.internal`, or a separate compose/stack you manage).
- Both services declare `extra_hosts: ["host.docker.internal:host-gateway"]`, which is a
  no-op on Docker Desktop but is what makes `host.docker.internal` resolve to the host on
  plain Linux Docker engines, where a host SQL Server would otherwise be unreachable.
- The image is framework-dependent (it runs on the `aspnet:10.0` runtime base), so a build
  adds only ~130 MB of unique layers on top of that shared base image.
- `packages/` at the repo root is a git-ignored staging folder used only during
  `build-local.ps1`; it is emptied again once the build finishes.
