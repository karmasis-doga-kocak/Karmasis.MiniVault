# Karmasis MiniVault — Advanced Installer setup

The Windows MSI for the MiniVault server. It performs the same six steps as
`deploy/windows/install.ps1`, but from an installer package instead of a PowerShell script.

| Path | What it is |
| --- | --- |
| `Karmasis.MiniVault/Karmasis.MiniVault.aip` | The Advanced Installer project (AI 22.x XML). |
| `Karmasis.MiniVault.CustomActions/` | Classic .NET Framework 4.8 class library with the three custom actions. |
| `Karmasis.MiniVault.CustomActions.Tests/` | net48 xUnit tests for that library. |
| `Karmasis.MiniVault.Setup.sln` | Solution holding those two projects. **Not** part of `Karmasis.MiniVault.sln`. |
| `verify-aip.ps1` | Static checks on the `.aip` (no Advanced Installer needed). |
| `img/` | Banner/background bitmaps and the icon, copied from the DAM Collector setup. |

## Status: authored, not built

**The `.aip` has never been opened in the Advanced Installer designer and no MSI has been
produced from it.** Advanced Installer is a licensed desktop product and is not installed on the
machine where these files were written, so the project file was authored as XML against the
schema used by
`Karmasis.Classic.DataskopeCollector/setups/AdvancedInstaller/DAM.Collector/DAM.Collector.aip`
(Advanced Installer 22.8). `verify-aip.ps1` checks everything that can be checked without the
product: XML well-formedness, the presence of the components the setup relies on, the custom
action rows and their binaries, and that every source path in the project resolves to a file that
exists after a publish. The custom-actions assembly **is** built and unit-tested here.

Expect the first designer session to normalize the file: Advanced Installer rewrites the `.aip`
on save, materializes the standard dialog control rows out of the fragments, and regenerates
component GUIDs for files it discovers through the synchronized folder. That is expected — commit
the rewritten file.

## Build order

The MSI build depends on two outputs that must exist first.

```powershell
# 1. The payload: self-contained win-x64 publish of the server.
dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64

# 2. The custom actions (see "Building the custom actions" for why this is msbuild, not dotnet).
msbuild setups\AdvancedInstaller\Karmasis.MiniVault.Setup.sln /t:Restore /p:Configuration=Release
msbuild setups\AdvancedInstaller\Karmasis.MiniVault.Setup.sln /t:Build   /p:Configuration=Release

# 3. Sanity check before handing the project to Advanced Installer.
.\setups\AdvancedInstaller\verify-aip.ps1

# 4. The MSI itself - needs Advanced Installer on the machine.
AdvancedInstaller.com /build setups\AdvancedInstaller\Karmasis.MiniVault\Karmasis.MiniVault.aip
```

Step 4 writes `setups/AdvancedInstaller/Karmasis.MiniVault/Setup Files/Karmasis.MiniVault.msi`
(`BuildComponent`: `PackageFolder="Setup Files"`, `PackageFileName="Karmasis.MiniVault"`).

**Task 5 (Azure Pipelines) needs an agent with Advanced Installer installed** — a licensed
`AdvancedInstaller.com` on `PATH`, or the `AdvancedInstaller@2`/`Advanced Installer Tool Installer`
marketplace task. The publish and custom-actions steps run on any Windows agent with the .NET 10
SDK and MSBuild; only step 4 is gated on Advanced Installer.

### Building the custom actions

`Karmasis.MiniVault.CustomActions.csproj` is a **classic (non-SDK) csproj**, deliberately, so it
matches `Karmasis.DataskopeCollector.CustomActions` in the Collector repo. That has one practical
consequence: `dotnet build` cannot build it. The .NET SDK's MSBuild does not import
`Microsoft.NuGet.targets`, which is what resolves `PackageReference` for legacy projects, so the
build fails with `CS0246: ... 'IMsiSession' could not be found` even after a successful restore.
Use MSBuild from Visual Studio / Visual Studio Build Tools (`vswhere -property installationPath`),
as above. The tests can then be run with the .NET CLI against the already-built output:

```powershell
dotnet test setups\AdvancedInstaller\Karmasis.MiniVault.CustomActions.Tests\Karmasis.MiniVault.CustomActions.Tests.csproj -c Release --no-build
```

Both configurations write to `bin\Release\`, on purpose: the paths in the `.aip` then never depend
on which configuration was built.

### The Karmasis.AdvancedInstallerKit package

The custom actions reference `Karmasis.AdvancedInstallerKit` 25.1.2, which lives on the Karmasis
private feed (artifactrepodev), not on nuget.org. The repo-root `nuget.config` does not list it,
so a clean agent needs a config that does — the same `nuget-dev.config` the Docker build uses for
`Karmasis.Cryptography` (see `docker/nuget.docker.config` for the pattern and Task 5 for the file
that supplies `NUGET_AUTH_TOKEN` / `VSS_NUGET_EXTERNAL_FEED_ENDPOINTS`):

```powershell
msbuild setups\AdvancedInstaller\Karmasis.MiniVault.Setup.sln /t:Restore /p:RestoreConfigFile=nuget-dev.config
```

On a developer machine that already has the package in `%USERPROFILE%\.nuget\packages`, restore
succeeds offline with the repo's own `nuget.config`.

## What the MSI does

1. Installs the publish output to `[ProgramFiles64Folder]Karmasis\MiniVault` (`APPDIR`), via a
   `SynchronizedFolderComponent` over `src/MiniVault.Server/bin/publish/win-x64`.
2. Creates `%ProgramData%\MiniVault` (`MsiCreateFolderComponent`) and applies SYSTEM +
   Administrators full control to it (`MsiLockPermissionsComponent`). The component is marked
   permanent, so **an uninstall leaves the folder, `appsettings.json` and the DPAPI-protected
   master key in place**.
3. `WriteMachineConfig` (deferred, no-impersonate, after `InstallFiles`) writes
   `%ProgramData%\MiniVault\appsettings.json` from the `MV_*` properties and re-applies the
   protected ACL — the same grants `install.ps1` applies with `icacls`, including the read/execute
   ACE for a service account that is not `LocalSystem`.
4. `RunInit` (deferred, no-impersonate) runs
   `minivault.exe init --recovery <mode> --out %ProgramData%\MiniVault\recovery-<timestamp>.txt`.
   A non-zero exit shows the CLI's own `Error:` line in the MSI error dialog and fails the install.
5. Registers the `KarmasisMiniVault` service (`MsiServInstComponent`: auto start, `LocalSystem` by
   default via `MV_SERVICEACCOUNT`, description, restart-three-times failure actions) and starts it
   (`MsiServCtrlComponent` event 160 = start on install, stop on uninstall).

`TestSqlConnection` (immediate, unsequenced) is there for a "Test connection" button: it opens the
connection string in `MV_CONNECTIONSTRING` with a 5-second timeout and sets `MV_SQL_OK` to `1` or
`0` plus `MV_SQL_ERROR`. It never fails an installation. Nothing calls it yet — see the designer
follow-ups.

There is no .NET Framework launch condition: the server publishes self-contained, so nothing has to
be installed on the target machine first.

### How the managed custom actions are invoked

A managed class library cannot be a Binary-table DLL custom action — it has no native entry point.
So, exactly as the Collector project does, the assembly is shipped through
`TempFileComponent` (extracted to `%TEMP%\[ProductCode]\`) and called through Advanced Installer's
own `DotNetMethodCaller.dll`:

* a `Type=51` property setter puts `NAME="value", ...;|[&<fileId>]|Namespace.Type.Method` into a
  property — for a deferred action the property must be **named after the action**, which is how MSI
  hands it its `CustomActionData`;
* the action itself is `Source="DotNetMethodCaller.dll" Target="CallDotNetMethod"` with
  `AdditionalSeq` pointing at its setter.

`MapCustomActionData<T>()` from the kit parses `NAME="value", NAME2="value2"`; the values **must** be
double-quoted, and a value containing a `"` will not round-trip. Keep quotes out of connection
strings and passwords passed to the MSI.

## MSI properties

| Property | Default | Meaning |
| --- | --- | --- |
| `MV_CONNECTIONSTRING` | *(empty, required)* | `ConnectionStrings:MiniVault`. |
| `MV_SERVICEACCOUNT` | `LocalSystem` | Account the service runs as; also the identity granted read access to `%ProgramData%\MiniVault`. |
| `MV_RECOVERY` | `single` | `single` or `shamir`. |
| `MV_SHARES` | `3` | Shamir shares (>= 2, <= 255). Ignored for `single`. |
| `MV_THRESHOLD` | `2` | Shamir threshold (>= 2, <= shares). Ignored for `single`. |
| `MV_MASTERKEY` | *(empty)* | Optional: derive the master key from this password instead of generating one. |
| `MV_CERT_PATH` | *(empty)* | PFX path. Exactly one of this or `MV_CERT_THUMBPRINT`. |
| `MV_CERT_PASSWORD` | *(empty)* | PFX password. Stored in plain text in `appsettings.json`. |
| `MV_CERT_THUMBPRINT` | *(empty)* | SHA-1 thumbprint of a certificate in `LocalMachine\My`. Spaces and the certmgr left-to-right mark are stripped. |
| `MV_URL` | `https://0.0.0.0:8200` | `Tls:Url`. |
| `MV_SQL_OK` / `MV_SQL_ERROR` | `0` / *(empty)* | Output of `TestSqlConnection`. |

`MV_CONNECTIONSTRING`, `MV_CERT_PASSWORD` and `MV_MASTERKEY` are listed in `MsiHiddenProperties`, so
they are not written to the MSI log. All `MV_*` properties are in `SecureCustomProperties` so they
survive into the deferred, elevated actions.

## Unattended install

Until the configuration pages exist, this is *the* way to install:

```powershell
msiexec /i Karmasis.MiniVault.msi /qn /l*v minivault-install.log ^
  MV_CONNECTIONSTRING="Server=sql01;Database=MiniVault;Integrated Security=true" ^
  MV_RECOVERY=shamir MV_SHARES=3 MV_THRESHOLD=2 ^
  MV_CERT_THUMBPRINT=0123456789ABCDEF0123456789ABCDEF01234567 ^
  MV_URL=https://0.0.0.0:8200
```

With a PFX instead of a certificate from the store:

```powershell
msiexec /i Karmasis.MiniVault.msi /qn ^
  MV_CONNECTIONSTRING="Server=sql01;Database=MiniVault;Integrated Security=true" ^
  MV_CERT_PATH="C:\certs\minivault.pfx" MV_CERT_PASSWORD="change-me"
```

The service account still needs a SQL login before the service can start; `install.ps1` prints the
script, and it is the same one:

```sql
CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
CREATE USER  [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
ALTER ROLE db_owner ADD MEMBER [NT AUTHORITY\SYSTEM];
```

## Recovery material

`RunInit` runs as a **deferred** custom action, and a deferred action cannot set MSI properties.
The recovery material therefore never reaches the installer UI. Instead:

* `minivault.exe init --out` writes it to `%ProgramData%\MiniVault\recovery-<timestamp>.txt`;
* the action logs an `InstallMessage.INFO` line naming that file.

**The operator must open that file, copy the recovery key (or the Shamir shares) to a safe offline
location, and then delete it.** The material cannot be retrieved again, and the file sits inside a
folder that is readable by SYSTEM and Administrators only. `install.ps1` deletes the file after the
operator types `SAVED`; the MSI cannot prompt from a deferred action, so deletion is manual.

## Uninstall

MSI stops and removes the `KarmasisMiniVault` service (`ServiceControl` event 160) and removes
`APPDIR`. `%ProgramData%\MiniVault` is **kept**: it holds `appsettings.json` and the DPAPI master
key, and deleting it would make the database unreadable after a reinstall. Remove it by hand when
decommissioning a host — the same behaviour as `deploy/windows/uninstall.ps1`.

## Designer follow-ups

These need the Advanced Installer designer and are deliberately **not** authored in the XML — the
Collector project has no custom pages to adapt, so hand-writing the `MsiControlComponent` /
`MsiControlEventComponent` rows for four new dialogs would have been guesswork.

1. **`SqlDlg`** — edit control bound to `MV_CONNECTIONSTRING`, plus a *Test connection* push button
   whose `DoAction` event runs `TestSqlConnection`, and a text control showing `[MV_SQL_ERROR]`
   conditioned on `MV_SQL_OK = "0"`.
2. **`MasterKeyDlg`** — optional masked edit bound to `MV_MASTERKEY`, and an edit for
   `MV_SERVICEACCOUNT`.
3. **`TlsDlg`** — `MV_URL`, a radio group choosing PFX vs. store, `MV_CERT_PATH` (with a browse
   button), masked `MV_CERT_PASSWORD`, `MV_CERT_THUMBPRINT`.
4. **`RecoveryDlg`** — radio group for `MV_RECOVERY` (`single` / `shamir`) with `MV_SHARES` and
   `MV_THRESHOLD` enabled only for `shamir`, and a finish-page note pointing at
   `%ProgramData%\MiniVault\recovery-*.txt`.

Insert them between `FolderDlg` and `VerifyReadyDlg` in the install sequence, and add validation so
`Next` is disabled while `MV_CONNECTIONSTRING` is empty or neither certificate property is set —
`WriteMachineConfig` rejects both cases, but failing in the UI is friendlier than failing mid-install.

Also worth doing in the designer: set `ARPPRODUCTICON` from `img/ds-48.ico` (the file is committed
but not yet referenced), and replace the Collector's bitmaps with MiniVault artwork.

## Tests

`Karmasis.MiniVault.CustomActions.Tests` covers the parts that are pure logic:

* `MachineConfigWriter` — the JSON for both certificate modes, the exact key names the server
  reads, escaping of `\`, `"` and control characters, thumbprint normalization, and the
  "exactly one certificate mode" rules.
* `ProcessRunner.Quote` — `CommandLineToArgvW` quoting, including trailing backslashes.
* `MiniVaultCli.BuildInitArguments` — `single`, `shamir`, `--master-key`, and the argument
  validation.
* `InstallActions` — `WriteMachineConfig` writing and protecting the folder, `RunInit` argument
  building and error reporting through a fake process runner, and `TestSqlConnection` against an
  unreachable server.

They run against a `FakeMsiSession` implementing the kit's `IMsiSession`, so no MSI session handle
is needed.
