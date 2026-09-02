# Karmasis.MiniVault

Minimal on-premises secret store for Karmasis services. The master key lives only on the MiniVault host; services fetch secrets over HTTP (TLS is configured by the installer and container images, see docs/operations.md) with a client identity.

The server, the operator CLI, and the HTTP API are implemented and tested; the Windows/Docker installer, TLS termination and a client library are still to come. See `docs/operations.md` for the CLI and the HTTP API reference.

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
