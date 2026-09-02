# Karmasis.MiniVault

Minimal on-premises secret store for Karmasis services. The master key lives only on the MiniVault host; services fetch secrets over HTTP (TLS is configured by the installer and container images, see docs/operations.md) with a client identity.

The server, the operator CLI, the HTTP API, and a .NET client library are implemented and tested; the Windows/Docker installer and TLS termination are still to come. See `docs/operations.md` for the CLI and the HTTP API reference.

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
