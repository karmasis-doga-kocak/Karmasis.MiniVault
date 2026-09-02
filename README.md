# Karmasis.MiniVault

Minimal on-premises secret store for Karmasis services. The master key lives only on the MiniVault host; services fetch secrets over HTTPS (see docs/operations.md, TLS) with a client identity.

The server, the operator CLI, the HTTP API, and a .NET client library are implemented and tested; the Windows/Docker installer is still to come. See `docs/operations.md` for the CLI and the HTTP API reference.

## Client library

`Karmasis.MiniVault.Client` (netstandard2.0; .NET Framework 4.7.2+ and .NET 8+) fetches
secrets from a MiniVault server, caches them for offline start, and retries once on an
expired token. See `docs/client.md` for setup, caching, background refresh, TLS pinning, and
error handling. It is not publishable to a shared feed yet: it depends on a prerelease
`Karmasis.Cryptography` because that is the only version shipping a `netstandard2.0` target — see
"Release prerequisites" in `docs/client.md`.

```csharp
using MiniVault.Client;

var client = MiniVaultClientFactory.Create(new MiniVaultOptions
{
    BaseUrl = "https://minivault.local:8200",
    ClientId = "dataskope-collector",
    ClientSecret = clientSecret,
});
var secret = await client.GetSecretAsync("dataskope/collector/connection-string");
```

## HTTP API

| Method | Path | Auth |
|---|---|---|
| `POST` | `/v1/auth/token` | none |
| `GET/PUT/DELETE` | `/v1/secrets/{name}` | Bearer |
| `GET` | `/v1/secrets?prefix=` | Bearer |
| `GET` | `/v1/health` | none |

### How a secret is read

1. The client presents a bearer token obtained from `/v1/auth/token`.
2. The server checks that one of the token's roles has a rule whose scope prefixes the secret's name.
3. The stored ciphertext's data key version is looked up and the matching DEK is fetched.
4. The value is decrypted with AES-GCM, using the secret's name as associated data so ciphertext cannot be moved to another name.
5. The read is written to the audit log, whether it succeeded or was denied.

## Build

    dotnet build
    dotnet test

## CI

`azure-pipelines.yml` runs on `dev` and `master` (no PR builds) on the `azure-self-hosted`
pool, following the same conventions as `Karmasis.Cryptography`'s pipeline:

1. **buildAndTest** — computes `image_version` from the `devops-vg` variable group
   (`vgMAJOR.vgMINOR.vgPATCH`, patch bumped by one), restores with `nuget-dev.config`
   (dev/feature branches) or `nuget-release.config` (master), builds, runs
   `dotnet test --collect:"XPlat Code Coverage"` and publishes the coverage via
   `PublishCodeCoverageResults@2`, then publishes the server
   (`dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64`) as the
   `minivault-win-x64` build artifact.
2. **packAndPublish** — packs *only* the three client packages
   (`MiniVault.Contracts`, `MiniVault.Client`, `MiniVault.Client.DependencyInjection`) with
   `versioningScheme: byEnvVar` / `image_version`, and pushes them to the internal feed
   (`artifactrepo` on master, `artifactrepodev` otherwise). On master it then bumps
   `vgPATCH`/`vgRC` in the variable group via `az pipelines variable-group variable update`.
   The server itself is not packed as a NuGet package.
3. **docker** (optional, gated on the `buildDocker` variable, default `false`) — builds and
   pushes the Docker image (`docker build -f docker/Dockerfile --build-arg
   NUGET_CONFIG=nuget-dev.config ...`). **Not yet usable as-is**: the Dockerfile's
   in-container `dotnet restore` needs credentials for the private feeds
   (`NUGET_AUTH_TOKEN` or `VSS_NUGET_EXTERNAL_FEED_ENDPOINTS`), which the pipeline does not
   yet provide — see the `TODO (DevOps team)` block in the `docker` stage.
4. **msi** (optional, gated on the `buildMsi` variable, default `false`) — builds the
   custom actions with MSBuild (`Karmasis.MiniVault.CustomActions` is a classic csproj;
   `dotnet build` cannot build it — see `setups/AdvancedInstaller/README.md`) and then the
   MSI itself via the `AdvancedInstaller@2` task. **Requires an agent with Advanced
   Installer installed** — it has never actually been built in this environment.

**Feed prerequisite:** `Karmasis.MiniVault.Client` currently depends on the prerelease
`Karmasis.Cryptography 26.3.0-dek.1`, published only to the developer-local folder feed
(`..\..\local-nuget`, used by the root `nuget.config`). `nuget-dev.config` and
`nuget-release.config` do not include that feed, so **CI restore of this branch will fail**
until `Karmasis.Cryptography` (ideally a stable release with a `netstandard2.0` target) is
pushed to the `artifactrepo`/`artifactrepodev` feeds. See `docs/client.md`, "Release
prerequisites".
