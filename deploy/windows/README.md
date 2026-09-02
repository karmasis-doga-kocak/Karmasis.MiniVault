# Windows install

Installs Karmasis MiniVault as a Windows service.

## 1. Publish

From the repo root, on Windows:

```
dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64
```

This produces a self-contained `win-x64` build in `src/MiniVault.Server/bin/publish/win-x64`, including
`minivault.exe` and `appsettings.json` (`appsettings.Development.json` is intentionally excluded from
publish output).

## 2. Install

Run `install.ps1` from an **elevated** PowerShell session (Windows PowerShell 5.1 or later; both scripts
declare `#Requires -Version 5.1`). It:

1. Copies the publish output into `-InstallDir` (`robocopy /MIR`) and creates `%ProgramData%\MiniVault`.
2. Writes `%ProgramData%\MiniVault\appsettings.json` with the connection string, `MasterKey:Provider=Dpapi`
   and the `Tls` section built from `-Url` and the certificate parameters.
3. Locks `%ProgramData%\MiniVault` down with a protected ACL, granted by **well-known SID** rather than by
   localized group name so the script works on non-English Windows: `*S-1-5-18` (SYSTEM) and
   `*S-1-5-32-544` (Administrators) get `(OI)(CI)F`, and any `-ServiceAccount` other than `LocalSystem`
   gets `(OI)(CI)RX` (`*S-1-5-20` for `NetworkService`, `*S-1-5-19` for `LocalService`, the account name
   otherwise). Read/execute is enough: the config and key file are written only by `init`/`recover`, which
   run as the operator. The server preserves that grant — `DpapiMasterKeyProvider` merges the explicit
   ACEs already present on the directory when it re-protects it, and copies them (read-only) onto
   `masterkey.bin`.
4. Runs `minivault.exe init` (via `Start-Process` with both streams captured, so a failure reports stderr),
   prints the recovery material, and asks the operator to type `SAVED` before continuing — the recovery
   material is shown only once and is not stored anywhere after this step. The `--out` file used to display
   it is deleted once you confirm. Pass `-NonInteractive` to skip the prompt (a warning is printed instead)
   for unattended installs; make sure the recovery output was captured some other way first. Pass `-SkipInit`
   to skip this step entirely — files, config, ACLs and the service are still installed, but no vault is
   created; use this when restoring an existing vault onto a new host (see `docs/operations.md`, "Backup and
   restore") and run `minivault.exe recover` yourself afterwards.
5. Prints the SQL grant script (see below), then registers the Windows service
   (`sc.exe create`/`description`/`failure`, restarting on failure) and starts it — unless
   `-SkipServiceStart` is passed, in which case the service is created but left stopped.
6. Waits up to 30 seconds for `https://localhost:<port>/v1/health` to answer, and prints the result. The
   probe uses `HttpClient` with a permissive certificate callback (`Invoke-WebRequest -SkipCertificateCheck`
   does not exist on Windows PowerShell 5.1). Skipped when `-SkipServiceStart` is used.

**Before** the service is created (between steps 4 and 5) the script prints the SQL script that grants the
service account access to the database (`CREATE LOGIN` / `CREATE USER` / `ALTER ROLE db_owner`). Run it on
the target SQL Server before the service starts, or the service cannot reach the database. For a strictly
ordered rollout pass `-SkipServiceStart`, run the SQL grant, then `sc.exe start <ServiceName>`.

### Example: certificate from a PFX file

```powershell
.\install.ps1 `
  -SourceDir C:\publish\minivault `
  -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" `
  -CertificatePath C:\certs\minivault.pfx `
  -CertificatePassword "the-pfx-password"
```

### Example: certificate already imported into a machine store

```powershell
.\install.ps1 `
  -SourceDir C:\publish\minivault `
  -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" `
  -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

### Preview without changing anything

`-WhatIfMode` prints the plan (install directory, ProgramData path, service name, and all six steps) and
exits without touching the machine. It does **not** require an elevated shell, which is also how
`InstallScriptTests` exercises the script in CI.

```powershell
.\install.ps1 -WhatIfMode -SourceDir C:\publish\minivault -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

### Parameters

| Parameter | Default | Notes |
|---|---|---|
| `-InstallDir` | `C:\Program Files\Karmasis\MiniVault` | |
| `-SourceDir` | (required) | Publish output folder. |
| `-ConnectionString` | (required) | Written to the machine config. A `Password=` in it triggers a warning (the file is ACL-protected but still plaintext). |
| `-ServiceAccount` | `LocalSystem` | `LocalSystem`/`NetworkService`/`LocalService` need no password; any other account needs `-ServiceAccountPassword` (validated up front) and must already have log-on-as-a-service rights. |
| `-ServiceAccountPassword` | | Required when `-ServiceAccount` is a real account. |
| `-Recovery` | `single` | `single` or `shamir`. |
| `-Shares` / `-Threshold` | | Required (both ≥ 2, `Threshold ≤ Shares ≤ 255`) when `-Recovery shamir`. |
| `-MasterKeyPassword` | | Optional: derive the master key from a password instead of a random one. |
| `-CertificatePath` / `-CertificatePassword` | | Exactly one of this pair or `-CertificateThumbprint` is required. The PFX password is stored in plain text in the (ACL-protected) ProgramData config; the script warns about this in yellow. |
| `-CertificateThumbprint` | | See above. Must normalize to 40 hex characters (spaces, colons and the invisible mark `certmgr.msc` copies are stripped). |
| `-Url` | `https://0.0.0.0:8200` | Must be an absolute `https://` URL. |
| `-ServiceName` | `KarmasisMiniVault` | |
| `-NonInteractive` | off | Skips the `SAVED` confirmation prompt (prints a warning instead). |
| `-SkipServiceStart` | off | Creates the service but does not start it, so the SQL grant can be applied first. Start it later with `sc.exe start <ServiceName>`. |
| `-SkipInit` | off | Skips step 4 (`minivault.exe init`) entirely. Use this to restore an existing vault onto a new host: run `minivault.exe recover` with the recovery material afterwards, instead of creating a brand-new vault. See `docs/operations.md`, "Backup and restore". |
| `-WhatIfMode` | off | Preview only; does not require elevation. |

## 3. Uninstall

```powershell
.\uninstall.ps1 -ServiceName KarmasisMiniVault -InstallDir "C:\Program Files\Karmasis\MiniVault"
```

Stops and deletes the service and removes `-InstallDir`. `%ProgramData%\MiniVault` (the DPAPI master key
and machine config) is left in place unless `-PurgeData` is also passed:

```powershell
.\uninstall.ps1 -ServiceName KarmasisMiniVault -InstallDir "C:\Program Files\Karmasis\MiniVault" -PurgeData -Force
```

`-PurgeData` prints a red warning and asks you to type `PURGE` before it deletes the master key. Without a
separately stored recovery key, every secret in the database becomes permanently unrecoverable — only
pass it when the vault (and its database) are also being decommissioned. `-Force` skips the confirmation
prompt for unattended teardown.
