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

Run `install.ps1` from an **elevated** PowerShell session. It:

1. Copies the publish output into `-InstallDir` (`robocopy /MIR`) and creates `%ProgramData%\MiniVault`.
2. Writes `%ProgramData%\MiniVault\appsettings.json` with the connection string, `MasterKey:Provider=Dpapi`
   and the `Tls` section built from `-Url` and the certificate parameters.
3. Locks `%ProgramData%\MiniVault` down with a protected ACL (SYSTEM, Administrators, and the service
   account when it isn't `LocalSystem`).
4. Runs `minivault.exe init`, prints the recovery material, and asks the operator to type `SAVED` before
   continuing — the recovery material is shown only once and is not stored anywhere after this step. The
   `--out` file used to display it is deleted once you confirm. Pass `-NonInteractive` to skip the prompt
   (a warning is printed instead) for unattended installs; make sure the recovery output was captured some
   other way first.
5. Registers the Windows service (`sc.exe create`/`description`/`failure`, restarting on failure) and
   starts it.
6. Waits up to 30 seconds for `https://localhost:<port>/v1/health` to answer, and prints the result.

At the end it prints a reminder SQL script to grant the service account access to the database
(`CREATE LOGIN` / `CREATE USER` / `ALTER ROLE db_owner`) — run that on the target SQL Server first if the
account doesn't already have access, or `init` (step 4) will fail.

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
| `-ServiceAccount` | `LocalSystem` | `LocalSystem`/`NetworkService`/`LocalService` need no password; any other account needs `-ServiceAccountPassword` and must already have log-on-as-a-service rights. |
| `-ServiceAccountPassword` | | Only used when `-ServiceAccount` is a real account. |
| `-Recovery` | `single` | `single` or `shamir`. |
| `-Shares` / `-Threshold` | | Required (both ≥ 2, `Threshold ≤ Shares ≤ 255`) when `-Recovery shamir`. |
| `-MasterKeyPassword` | | Optional: derive the master key from a password instead of a random one. |
| `-CertificatePath` / `-CertificatePassword` | | Exactly one of this pair or `-CertificateThumbprint` is required. |
| `-CertificateThumbprint` | | See above. |
| `-Url` | `https://0.0.0.0:8200` | Must be an absolute `https://` URL. |
| `-ServiceName` | `KarmasisMiniVault` | |
| `-NonInteractive` | off | Skips the `SAVED` confirmation prompt (prints a warning instead). |
| `-WhatIfMode` | off | Preview only; does not require elevation. |

## 3. Uninstall

```powershell
.\uninstall.ps1 -ServiceName KarmasisMiniVault -InstallDir "C:\Program Files\Karmasis\MiniVault"
```

Stops and deletes the service and removes `-InstallDir`. `%ProgramData%\MiniVault` (the DPAPI master key
and machine config) is left in place unless `-PurgeData` is also passed:

```powershell
.\uninstall.ps1 -ServiceName KarmasisMiniVault -InstallDir "C:\Program Files\Karmasis\MiniVault" -PurgeData
```

`-PurgeData` prints a warning and then deletes the master key. Without a separately stored recovery key,
every secret in the database becomes permanently unrecoverable — only pass it when the vault (and its
database) are also being decommissioned.
