# Karmasis.MiniVault

MiniVault is an on-premises secret store for Karmasis services: a small .NET server, a SQL Server
database, and a client library. The master key that protects everything lives only on the MiniVault
host — never in the database, never in a consuming service. A service authenticates with a client id
and a client secret, gets a short-lived token, and reads the secrets its roles allow over HTTPS.

## The journey of one secret

```
1. The client POSTs its id and secret to /v1/auth/token and gets a JWT valid for 15 minutes.
2. It calls GET /v1/secrets/{name} with that token in an Authorization: Bearer header.
3. The server checks the token's roles for a rule whose scope is a prefix of {name}.
4. It reads the secret row, takes its DekVersion, and unwraps that data key with the master key.
5. It decrypts the value with AES-256-GCM, using the secret's name as associated data.
6. It writes an audit row — success or denial — and returns the value inside the TLS connection.
```

Step 5 is why a ciphertext cannot be moved to another name: the name is part of what the tag covers.

## Where the master key lives

On Windows it is a file, `%ProgramData%\MiniVault\masterkey.bin`, protected with DPAPI at
`LocalMachine` scope and restricted by ACL to SYSTEM, Administrators and the service account. DPAPI
binds it to the machine, so a copy of the file is useless on any other host. In a container there is
no DPAPI, so the key is the base64 `MINIVAULT__MASTERKEY` environment variable instead. In neither
case is the master key stored in the database. If it is lost, the recovery material printed by
`minivault init` is what replaces it.

## When you need the recovery material

If the master key is lost — a rebuilt host, a deleted file, a moved database — or you simply want to
change it, run `minivault recover` with the recovery key (or enough Shamir shares). It builds a new
master key and rewraps every data key with it; the secrets themselves are untouched. So back up two
things, and keep them apart: the SQL Server database, and the recovery material. Either one alone is
useless, and losing both means the secrets are gone for good.

## Roles

A role is a name plus a list of rules; each rule is a scope prefix and a permission, `read` or
`write` (write includes read). A client is assigned zero or more roles, and may act on a secret when
any of its roles has a rule whose scope is a prefix of the secret's name. Matching is an ordinal
prefix comparison, so end scopes with `/`: the scope `dataskope` also covers `dataskope-other/x`,
while `dataskope/` does not. The empty scope covers the whole vault and has to be asked for
explicitly with `--all`.

## Repository layout

| Path | What it is |
|---|---|
| `src/Karmasis.MiniVault.Server` | The server and the operator CLI. .NET 10 minimal API, EF Core, SQL Server. Produces `minivault.exe`. |
| `src/Karmasis.MiniVault.Contracts` | Request/response DTOs shared by the server and the client. `netstandard2.0`. |
| `src/Karmasis.MiniVault.Client` | `Karmasis.MiniVault.Client`: the consuming-service library, with caching and offline start. `netstandard2.0`. |
| `src/Karmasis.MiniVault.Client.DependencyInjection` | `AddMiniVaultClient` for `Microsoft.Extensions.DependencyInjection`. |
| `test/Karmasis.MiniVault.Server.Tests` | Server unit and integration tests (xUnit, Shouldly, LocalDB). |
| `test/Karmasis.MiniVault.Client.Tests` | Client tests against a stubbed `HttpMessageHandler`. |
| `deploy/windows` | `install.ps1` / `uninstall.ps1` for a scripted Windows service install. |
| `docker` | `Dockerfile`, `docker-compose.yml`, and a local build script. |
| `setups/AdvancedInstaller` | The MSI project (`.aip`) and its net48 custom actions. |
| `docs` | `operations.md`, `client.md`, `design.md`. |
| `azure-pipelines.yml` | The CI definition. |

## Build and test

```powershell
dotnet build
dotnet test
```

You need the .NET SDK 10 (`global.json` pins 10.0.301 with `latestFeature` roll-forward) and SQL
Server LocalDB — the server's integration tests create throw-away databases on
`(localdb)\MSSQLLocalDB`.

Restore currently needs a local folder feed. `nuget.config` adds `..\..\local-nuget` (that is
`D:\Karmasis\local-nuget` for a checkout at `D:\Karmasis\repos\Karmasis.MiniVault`), which is where
the prerelease `Karmasis.Cryptography 26.3.0-dek.1` lives — the only version with a `netstandard2.0`
target. Until that package is published to the internal feeds, CI restore of this branch fails; see
"Release prerequisites" in `docs/client.md`.

## Run it locally in five minutes

Initialize a development vault. The master key goes to a temporary path so it does not collide with
a real install under `%ProgramData%`:

```powershell
dotnet run --project src/Karmasis.MiniVault.Server -- init --recovery single `
  --ConnectionStrings:MiniVault "Server=(localdb)\MSSQLLocalDB;Database=MiniVaultDev;Integrated Security=true;TrustServerCertificate=true" `
  --MasterKey:Provider Dpapi `
  --MasterKey:Path "$env:TEMP\minivault-dev\masterkey.bin"
```

Copy the `Recovery key:` line it prints. Then create a role, grant it a scope, and create a client.
Every operator command reads the same configuration, so the two overrides are repeated each time
(shortened to `...` here for width):

```powershell
$db = "Server=(localdb)\MSSQLLocalDB;Database=MiniVaultDev;Integrated Security=true;TrustServerCertificate=true"
$key = "$env:TEMP\minivault-dev\masterkey.bin"

dotnet run --project src/Karmasis.MiniVault.Server -- role add dev-reader --ConnectionStrings:MiniVault $db --MasterKey:Path $key
dotnet run --project src/Karmasis.MiniVault.Server -- role grant dev-reader --scope dev/ --permission write --ConnectionStrings:MiniVault $db --MasterKey:Path $key
dotnet run --project src/Karmasis.MiniVault.Server -- client add dev-app --role dev-reader --ConnectionStrings:MiniVault $db --MasterKey:Path $key
```

`client add` prints the client secret once. Now start the server. It listens on HTTPS only, so in
development it borrows the ASP.NET Core development certificate — which is allowed only in the
Development environment:

```powershell
dotnet dev-certs https --trust    # once per machine
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Karmasis.MiniVault.Server -- --ConnectionStrings:MiniVault $db --MasterKey:Path $key --Tls:AllowDevelopmentCertificate true
```

`appsettings.Development.json` sets `Tls:Url` to `https://localhost:8200`. From a second shell, get a
token, write a secret, and read it back:

```powershell
Set-Content token.json -Encoding ascii -Value '{"clientId":"dev-app","clientSecret":"<the secret client add printed>"}'
$token = (curl.exe -sk -X POST https://localhost:8200/v1/auth/token -H "Content-Type: application/json" -d "@token.json" | ConvertFrom-Json).accessToken

Set-Content value.json -Encoding ascii -Value '{"value":"aGVsbG8=","contentType":"text/plain"}'
curl.exe -sk -X PUT https://localhost:8200/v1/secrets/dev/greeting -H "Authorization: Bearer $token" -H "Content-Type: application/json" -d "@value.json"
# {"version":1}

curl.exe -sk https://localhost:8200/v1/secrets/dev/greeting -H "Authorization: Bearer $token"
# {"name":"dev/greeting","value":"aGVsbG8=","contentType":"text/plain","version":1,"updatedAt":"..."}

Remove-Item token.json, value.json
```

The bodies go through files on purpose: Windows PowerShell 5.1 mangles the double quotes in a JSON
string handed to a native executable, and the server answers `invalid_request`. Secret values travel
as base64 in both directions — `aGVsbG8=` is `hello`. `-k` is needed because the development
certificate is not in `curl.exe`'s trust store; a real client trusts or pins the certificate instead
(`docs/client.md`, section 11). Delete the two body files afterwards: `token.json` holds the client
secret.

## Client library

```csharp
using Karmasis.MiniVault.Client;

var client = MiniVaultClientFactory.Create(new MiniVaultOptions
{
    BaseUrl = "https://minivault.local:8200",
    ClientId = "dataskope-collector",
    ClientSecret = clientSecret,
});
var secret = await client.GetSecretAsync("dataskope/collector/connection-string");
```

`Karmasis.MiniVault.Client` targets `netstandard2.0`, so it runs on .NET Framework 4.7.2+ and .NET
8+. It caches secrets in memory and, optionally, in an encrypted file, so a service can start while
the server is down. See `docs/client.md` for setup, caching, background refresh, TLS pinning and
error handling. It cannot be published to a shared feed yet: it depends on the prerelease
`Karmasis.Cryptography 26.3.0-dek.1` because that is the only version with a `netstandard2.0`
target, and a stable release with that target has to come first.

## Deployment

**Windows service.** `dotnet publish src/Karmasis.MiniVault.Server -p:PublishProfile=win-x64` produces a
self-contained folder. `deploy/windows/install.ps1` copies it into place, writes
`%ProgramData%\MiniVault\appsettings.json`, locks that folder down, runs `init`, prints the SQL grant
script, and registers and starts the `KarmasisMiniVault` service. Re-running it upgrades in place.
The MSI in `setups/AdvancedInstaller` does the same work from `MV_*` properties, with a four-page
wizard (SQL connection, service account and master key, HTTPS certificate, recovery mode) for an
interactive first install. The `.aip` builds into an MSI with Advanced Installer's command line
(`setups/AdvancedInstaller/verify-aip.ps1 -Build`) and the custom actions are tested, but the MSI
has not been installed on any machine and the pages have not been looked at yet; the remaining
checks are listed in `setups/AdvancedInstaller/README.md`.

**Docker.** `docker/Dockerfile` builds a Linux image on `mcr.microsoft.com/dotnet/aspnet:10.0` that
runs as a non-root user, takes its master key from `MINIVAULT__MASTERKEY`, and mounts a PFX for TLS.
`docker/docker-compose.yml` has an `init` profile for the one-shot initialization run.

Neither path has been exercised on a production-shaped host from this machine. `docs/operations.md`
carries the full procedures and a pre-production checklist of what still has to be verified on an
elevated Windows host, on a machine with Advanced Installer, on a CI agent, and in a container.

## Documents

- `docs/operations.md` — installation, CLI reference, master key providers, backup and restore, TLS, the HTTP API, upgrading, troubleshooting, pre-production checklist.
- `docs/client.md` — the client library: setup, cache, background refresh, errors, TLS pinning.
- `docs/design.md` — the V1 design specification (Turkish).
- `deploy/windows/README.md`, `docker/README.md`, `setups/AdvancedInstaller/README.md` — per-target deployment notes.

## CI

`azure-pipelines.yml` runs on `dev` and `master` (no PR builds) on the `azure-self-hosted` pool.
**buildAndTest** computes `image_version` from the `devops-vg` variable group, restores with
`nuget-dev.config` (dev/feature branches) or `nuget-release.config` (master), builds, tests with
coverage, and publishes the win-x64 server as the `minivault-win-x64` artifact. **packAndPublish**
packs only the three client packages and pushes them to the internal feed. **docker** and **msi**
are optional stages, gated on the `buildDocker` and `buildMsi` variables (both `false` by default):
the docker stage still needs feed credentials inside the container, and the msi stage needs an agent
with Advanced Installer. Restore fails on any agent until `Karmasis.Cryptography` is published to
`artifactrepo`/`artifactrepodev`.
