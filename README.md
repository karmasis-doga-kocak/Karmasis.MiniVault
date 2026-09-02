# Karmasis.MiniVault

Minimal on-premises secret store for Karmasis services. The master key lives only on the MiniVault host; services fetch secrets over HTTPS with a client identity.

Status: under construction. See `docs/operations.md` for the CLI.

## Build

    dotnet build
    dotnet test
