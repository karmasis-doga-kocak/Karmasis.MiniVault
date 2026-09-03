# Karmasis MiniVault — Advanced Installer setup

The Windows MSI for the MiniVault server. It performs the same six steps as
`deploy/windows/install.ps1`, but from an installer package instead of a PowerShell script.

| Path | What it is |
| --- | --- |
| `Karmasis.MiniVault/Karmasis.MiniVault.aip` | The Advanced Installer project (AI 22.x XML). |
| `Karmasis.MiniVault.CustomActions/` | net48 class library (SDK-style csproj) with the custom actions. Part of `Karmasis.MiniVault.sln`. |
| `Karmasis.MiniVault.CustomActions.Tests/` | net48 xUnit tests for that library. Part of `Karmasis.MiniVault.sln`. |
| `verify-aip.ps1` | Static checks on the `.aip` (no Advanced Installer needed). |
| `img/` | Banner/background bitmaps and the icon, copied from the DAM Collector setup. |

## Status: builds from the command line, not yet installed anywhere

The `.aip` was authored as XML (against the schema of
`Karmasis.Classic.DataskopeCollector/setups/AdvancedInstaller/DAM.Collector/DAM.Collector.aip`,
Advanced Installer 22.8) and **builds into an MSI with Advanced Installer 24.0's command line**
(`verify-aip.ps1 -Build`, see "Build order"). The loader errors that surfaced on the way — an empty
`Property.Value`, the `MsiLockPermComponent` schema, the missing `AI_*_SETUPEXEPATH` actions, the
short|long `FileName` — are fixed and each has a check in `verify-aip.ps1`. So is the first thing a
test run of the MSI showed: the theme fragments supply the standard dialogs but none of their
page-to-page events, so `WelcomeDlg`'s Next did nothing until the Collector's explicit
`NewDialog`/`EndDialog` rows were added (`verify-aip.ps1` now requires the whole chain). The built MSI's
`Dialog`, `Control`, `ControlEvent`, `ControlCondition` and `CheckBox` tables were inspected with the
Windows Installer API: the four configuration pages, their navigation, the check-box refresh
(`[AiRefreshDlg]` becomes a `NewDialog` to a generated `<Dialog>_1` clone and back) and the
`FolderDlg`/`VerifyReadyDlg` overrides are all present and the theme fragment's default rows are
gone. What has **not** happened yet: the MSI has not been installed on any machine, and nobody has
looked at the pages — layout is by coordinates only.

The build prints twenty `AI_ICE07` lines ("property associated to control ... does not have a
default value"): one per edit/check box bound to a property that deliberately has no default (the
connection string, every password, the PFX path/thumbprint, the UI-only check boxes). Advanced
Installer still builds the package (exit code 0). They are noise here, and `verify-aip.ps1 -Build`
prints them as `[info]`.

The tracked `.aip` is now the file as the Advanced Installer 24.0 designer saved it: converted to
the 24.0 format, XML comments stripped, and the publish output materialized into explicit
`MsiFilesComponent` / `MsiCompsComponent` rows with generated GUIDs next to the
`SynchronizedFolderComponent` (that is why it grew from 60 KB to 270 KB). The narrative that used to
live in the comments is in this README. Two things to know when working in the designer:

- **Saving writes what the designer loaded.** The designer keeps the project in memory; if the file
  changes on disk while it is open (a `git pull`, an edit here) and you then save, those changes are
  gone. Close the project before pulling, and run `verify-aip.ps1 -Build` after saving — it fails if
  the wizard navigation or any of the checks above went missing, which is exactly what happened
  the first time.
- The designer creates `*.back(<version>).aip` next to the project; they are git-ignored.

## Build order

The MSI build depends on two outputs that must exist first.

```powershell
# 1. The payload: self-contained win-x64 publish of the server.
dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64

# 2. The custom actions. Part of Karmasis.MiniVault.sln, so a plain `dotnet build` at the repo root
#    builds them too; this builds just the one project (see "Building the custom actions").
dotnet build setups\AdvancedInstaller\Karmasis.MiniVault.CustomActions\Karmasis.MiniVault.CustomActions.csproj -c Release

# 3. Sanity check before handing the project to Advanced Installer.
.\setups\AdvancedInstaller\verify-aip.ps1

# 4. The MSI itself - needs Advanced Installer on the machine. Either directly:
AdvancedInstaller.com /build setups\AdvancedInstaller\Karmasis.MiniVault\Karmasis.MiniVault.aip
#    or through the check script, which builds a scratch copy (<name>.check.aip) so the tracked
#    project is not converted/normalized by the build, and reports loader errors as failures:
.\setups\AdvancedInstaller\verify-aip.ps1 -Build
```

Step 4 writes `setups/AdvancedInstaller/Karmasis.MiniVault/Setup Files/Karmasis.MiniVault.msi`
(`BuildComponent`: `PackageFolder="Setup Files"`, `PackageFileName="Karmasis.MiniVault"`).

**Task 5 (Azure Pipelines) needs an agent with Advanced Installer installed** — a licensed
`AdvancedInstaller.com` on `PATH`, or the `AdvancedInstaller@2`/`Advanced Installer Tool Installer`
marketplace task. The publish and custom-actions steps run on any Windows agent with the .NET 10
SDK; only step 4 is gated on Advanced Installer.

### Building the custom actions

`Karmasis.MiniVault.CustomActions.csproj` is an SDK-style project targeting `net48`, and it is part
of `Karmasis.MiniVault.sln` together with its test project — `dotnet build` and `dotnet test` at the
repo root cover both. (The Collector repo keeps its custom actions as a classic, non-SDK csproj;
that format cannot be built with `dotnet build`, which is why this one differs. Advanced Installer's
`DotNetMethodCaller.dll` only needs a net48 assembly and does not care about the project format.)

The assembly attributes live in `Properties\AssemblyInfo.cs` (`GenerateAssemblyInfo` is off), and
both configurations write to `bin\Release\` on purpose (`AppendTargetFrameworkToOutputPath` is
off): the paths in the `.aip` then never depend on which configuration was built. The tests are
net48 and so run only on Windows; on Linux they build but cannot execute.

To run just these tests:

```powershell
dotnet test setups\AdvancedInstaller\Karmasis.MiniVault.CustomActions.Tests\Karmasis.MiniVault.CustomActions.Tests.csproj -c Release
```

### The Karmasis.AdvancedInstallerKit package

The custom actions reference `Karmasis.AdvancedInstallerKit` 25.1.2, which lives on the Karmasis
private feed (artifactrepodev), not on nuget.org. The repo-root `nuget.config` does not list it,
so a clean agent needs a config that does — the same `nuget-dev.config` the Docker build uses for
`Karmasis.Cryptography` (see `docker/nuget.docker.config` for the pattern and Task 5 for the file
that supplies `NUGET_AUTH_TOKEN` / `VSS_NUGET_EXTERNAL_FEED_ENDPOINTS`):

```powershell
dotnet restore Karmasis.MiniVault.sln --configfile nuget-dev.config
```

On a developer machine that already has the package in `%USERPROFILE%\.nuget\packages`, restore
succeeds offline with the repo's own `nuget.config`.

## What the MSI does

1. Installs the publish output to `[ProgramFiles64Folder]Karmasis\MiniVault` (`APPDIR`), via a
   `SynchronizedFolderComponent` over `src/MiniVault.Server/bin/publish/win-x64`.
   `minivault.exe` itself is **excluded** from that synchronized folder and listed as a static
   `MsiFilesComponent` row instead, so exactly one row owns it. It cannot be left to the
   synchronized folder: `ServiceInstall`, `ServiceControl` and `ServiceConfig` all hang off a
   component whose `KeyPath` is that file, and the components a synchronized folder creates at build
   time have generated names nothing in the project can reference. Every component is marked
   `Attributes` 64-bit (`256`), because the package is `MsiPackageType="x64"`.
2. Creates `%ProgramData%\MiniVault` (`MsiCreateFolderComponent`) and applies SYSTEM +
   Administrators full control to it (`MsiLockPermissionsComponent`). The component is marked
   permanent, so **an uninstall leaves the folder, `appsettings.json` and the DPAPI-protected
   master key in place**.
3. `WriteMachineConfig` (deferred, no-impersonate, after `InstallFiles`) writes
   `%ProgramData%\MiniVault\appsettings.json` from the `MV_*` properties and re-applies the
   protected ACL — the same grants `install.ps1` applies with `icacls`, including the read/execute
   ACE for a service account that is not `LocalSystem`.
   **If the file already exists it is kept**, and only the ACL is re-applied: an upgrade started from
   Add/Remove Programs supplies the default `MV_*` values, and writing those over a working
   configuration would take the server down. Pass `MV_RECONFIGURE=1` to overwrite it deliberately.
4. `RunInit` (deferred, no-impersonate) runs
   `minivault.exe init --recovery <mode> --out %ProgramData%\MiniVault\recovery-<timestamp>.txt`.
   A non-zero exit shows the CLI's own `Error:` line in the MSI error dialog and fails the install —
   **except** when the output indicates the vault is already initialized (the server's
   `VaultAlreadyInitializedException` message), which happens on an upgrade over an existing,
   already-initialized install: that case is treated as a no-op (an `INFO` message is logged and
   the action succeeds) instead of failing the upgrade.
5. Registers the `KarmasisMiniVault` service (`MsiServInstComponent`: auto start, `LocalSystem` by
   default via `MV_SERVICEACCOUNT`, `MV_SERVICEACCOUNT_PASSWORD` in the `Password` column, description,
   restart-three-times failure actions) and starts it (`MsiServCtrlComponent` event `163` = `0x01`
   start on install + `0x02` stop on install + `0x20` stop on uninstall + `0x80` delete on uninstall).
   The `0x01` bit is what actually starts the service; the earlier value `160` had only the uninstall
   bits, so the MSI registered a service it never started.

`TestSqlConnection` (immediate, unsequenced) is there for a "Test connection" button: it opens the
connection string in `MV_CONNECTIONSTRING` with a 5-second timeout and sets `MV_SQL_OK` to `1` or
`0` plus `MV_SQL_ERROR` / `MV_SQL_NOTE`. A database that does not exist yet is the normal
first-install case (`minivault init` creates it), so SQL error 4060 ("Cannot open database ...
requested by the login") is followed up against `master`: the test passes with a note when the
database is absent and the login may create databases (sysadmin, dbcreator or CREATE ANY DATABASE),
and fails with a message saying which when the database exists but the login cannot open it, or when
the login cannot create it. It never fails an installation. The *Test connection* button on `SqlDlg`
runs it (see "Dialogs").

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
double-quoted, and a value containing a `"` will not round-trip. That is enforced rather than left to
the operator: the immediate `ValidateProperties` custom action (sequenced right after
`AI_ExtractTempFiles` - which is what extracts this action's own DLL - and before `InstallInitialize`,
so nothing has been installed yet) fails the installation with a message naming the
property when `MV_CONNECTIONSTRING`, `MV_CERT_PASSWORD`, `MV_MASTERKEY` or `MV_SERVICEACCOUNT_PASSWORD`
contains one.

The master-key password is the one value that does **not** travel to `minivault.exe` on a command
line: `RunInit` builds `init ... --master-key-from-env` and puts the password in the child's
`MINIVAULT_INIT_MASTER_KEY` environment variable, which the CLI clears from its own environment as soon
as it has read it. A deferred custom action's child command line is visible in the process list and is
written to the MSI verbose log.

## MSI properties

| Property | Default | Meaning |
| --- | --- | --- |
| `MV_CONNECTIONSTRING` | *(empty, required)* | `ConnectionStrings:MiniVault`. |
| `MV_SERVICEACCOUNT` | `LocalSystem` | Account the service runs as; also the identity granted read access to `%ProgramData%\MiniVault`. |
| `MV_SERVICEACCOUNT_PASSWORD` | *(empty)* | Password for `MV_SERVICEACCOUNT`, written straight into the `ServiceInstall` `Password` column. Leave empty for the built-in accounts (`LocalSystem`, `NT AUTHORITY\NetworkService`, ...). The MSI does **not** grant `SeServiceLogonRight` ("Log on as a service") to a non-built-in account — a domain or local service account must already have it **before** the install (grant it in `secpol.msc`, or run `deploy/windows/install.ps1` once, which grants it), otherwise the service-install custom action fails and the MSI rolls back with error 1920 (wrapping SCM error 1069, "The service did not start due to a logon failure"). |
| `MV_RECOVERY` | `single` | `single` or `shamir`. |
| `MV_SHARES` | `3` | Shamir shares (>= 2, <= 255). Ignored for `single`. |
| `MV_THRESHOLD` | `2` | Shamir threshold (>= 2, <= shares). Ignored for `single`. |
| `MV_MASTERKEY` | *(empty)* | Optional: derive the master key from this password instead of generating one. |
| `MV_CERT_PATH` | *(empty)* | PFX path. Exactly one of this or `MV_CERT_THUMBPRINT`. |
| `MV_CERT_PASSWORD` | *(empty)* | PFX password. Stored in plain text in `appsettings.json`. |
| `MV_CERT_THUMBPRINT` | *(empty)* | SHA-1 thumbprint of a certificate in `LocalMachine\My`. Spaces and the certmgr left-to-right mark are stripped. |
| `MV_URL` | `https://0.0.0.0:8200` | `Tls:Url`. |
| `MV_RECONFIGURE` | *(empty)* | `1` to overwrite an existing `%ProgramData%\MiniVault\appsettings.json`. Empty (the default) keeps it, so an upgrade never clobbers a working configuration. |
| `MV_SQL_OK` / `MV_SQL_ERROR` / `MV_SQL_NOTE` / `MV_SQL_RESULT` | `0` / *(empty)* | Output of `TestSqlConnection`. |
| `MV_SQL_SERVER`, `MV_SQL_DATABASE`, `MV_SQL_AUTH`, `MV_SQL_USER`, `MV_SQL_PASSWORD`, `MV_SQL_ENCRYPT`, `MV_SQL_TRUSTCERT` | *(empty)*, `MiniVault`, `windows`, *(empty)*, *(empty)*, `1`, *(empty)* | The parts `BuildConnectionString` composes `MV_CONNECTIONSTRING` from — what the SQL page asks for. A silent install may pass these instead of `MV_CONNECTIONSTRING`: `BuildConnectionStringSilent` runs before `ValidateProperties` and composes the string whenever `MV_SQL_SERVER` is set (and `MV_SQL_ADVANCED` is not `1`); with `MV_SQL_SERVER` empty it leaves `MV_CONNECTIONSTRING` alone. `MV_SQL_AUTH` is `windows` or `sql`. |
| `MV_SQL_ADVANCED` | *(empty)* | `1` = the SQL page's "enter the connection string directly" mode; `MV_CONNECTIONSTRING` is then used as typed. |
| `MV_SERVICEACCOUNT_KIND` | `LocalSystem` | UI only: the service-account radio group (`LocalSystem` / `NetworkService` / `Custom`). The service page's Next translates it into `MV_SERVICEACCOUNT`; a silent install sets `MV_SERVICEACCOUNT` directly. |

Only the properties with a default have a row in the Property table: `Value` is a required column and
Advanced Installer refuses to open a project with an empty one ("Required column [Property.Value] has
empty value"). A property without a row reads as empty, which is exactly the "(empty)" default above;
`verify-aip.ps1` fails on an empty-`Value` row.

`MV_CONNECTIONSTRING`, `MV_CERT_PASSWORD`, `MV_MASTERKEY` and `MV_SERVICEACCOUNT_PASSWORD` are listed
in `MsiHiddenProperties`, and so are `WriteMachineConfig` and `RunInit` — a deferred action reads its
input from a property named after the action, and MSI logs that property like any other, so hiding
only the `MV_*` properties would still leave full copies of every secret in a `/l*v` log. All `MV_*`
properties are in `SecureCustomProperties` so they survive into the deferred, elevated actions.

None of these values may contain a double quote. `CustomActionData` is a `NAME="value"` list, so a
quote would truncate the value; the immediate `ValidateProperties` custom action (sequenced right
after `AI_ExtractTempFiles` and before `InstallInitialize`) fails the installation with a message
naming the offending property rather than letting a truncated connection string or password reach the
deferred actions. If a connection-string value needs `;` or `=` inside it, use single quotes there
instead of double quotes — SqlClient accepts single-quoted values in a connection string, and they
survive the `NAME="value"` list untouched.

## Unattended install

For scripted or automated hosts, and for any install that should not show the wizard:

```powershell
msiexec /i Karmasis.MiniVault.msi /qn /l*v minivault-install.log `
  MV_CONNECTIONSTRING="Server=sql01;Database=MiniVault;Integrated Security=true" `
  MV_RECOVERY=shamir MV_SHARES=3 MV_THRESHOLD=2 `
  MV_CERT_THUMBPRINT=0123456789ABCDEF0123456789ABCDEF01234567 `
  MV_URL=https://0.0.0.0:8200
```

The same, passing the SQL connection as parts instead of a string (composed by
`BuildConnectionStringSilent` before validation; `MV_SQL_AUTH=windows` needs no login):

```powershell
msiexec /i Karmasis.MiniVault.msi /qn `
  MV_SQL_SERVER=sql01 MV_SQL_DATABASE=MiniVault MV_SQL_AUTH=sql MV_SQL_USER=minivault_setup MV_SQL_PASSWORD=... MV_SQL_TRUSTCERT=1 `
  MV_CERT_THUMBPRINT=0123456789ABCDEF0123456789ABCDEF01234567
```

With a PFX instead of a certificate from the store:

```powershell
msiexec /i Karmasis.MiniVault.msi /qn `
  MV_CONNECTIONSTRING="Server=sql01;Database=MiniVault;Integrated Security=true" `
  MV_CERT_PATH="C:\certs\minivault.pfx" MV_CERT_PASSWORD="change-me"
```

(PowerShell continues a line with a backtick; from `cmd.exe` use `^` instead. A verbose log written by
an MSI built **before** the `MsiHiddenProperties` fix contains every secret above in clear text — treat
such a log as a secret and rotate what it exposed.)

The service account still needs a SQL login before the service can start; `install.ps1` prints the
script, and it is the same one. The running service only reads and writes rows:

```sql
CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
CREATE USER  [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
ALTER ROLE db_datareader ADD MEMBER [NT AUTHORITY\SYSTEM];
ALTER ROLE db_datawriter ADD MEMBER [NT AUTHORITY\SYSTEM];
```

DDL rights belong to whoever runs `init`/`migrate` (the MSI's `RunInit` runs as the elevated installer
account, not as the service), not to the service itself.

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

MSI stops and removes the `KarmasisMiniVault` service (`ServiceControl` event `163`, bits `0x20` stop
on uninstall + `0x80` delete on uninstall) and removes `APPDIR`. `%ProgramData%\MiniVault` is
**kept** — the `MiniVaultData` component is marked permanent — because it holds `appsettings.json` and
the DPAPI master key, and deleting it would make the database unreadable after a reinstall. The same
behaviour as `deploy/windows/uninstall.ps1` without `-PurgeData`.

### Decommissioning a host

Uninstalling is not decommissioning. To retire a host for good, after the MSI uninstall:

1. Confirm the recovery material is stored offline — deleting the master key without it makes every
   secret in the database permanently unreadable.
2. Delete `%ProgramData%\MiniVault` by hand (it holds `appsettings.json`, `masterkey.bin`, and any
   `recovery-*.txt` you have not removed yet).
3. Drop the SQL login/user for the service account, and the MiniVault database itself if nothing else
   will use it.
4. Remove the server certificate from `LocalMachine\My` if it was imported for MiniVault only.

## Dialogs

On a first install the wizard runs `WelcomeDlg → FolderDlg → SqlDlg → ServiceDlg → TlsDlg →
RecoveryDlg → VerifyReadyDlg → ProgressDlg → ExitDialog`. The four configuration pages are authored
in the `.aip` (`MsiDialogComponent`, `MsiControlComponent`, `MsiControlConditionComponent`,
`MsiControlEventComponent`, `MsiCheckBoxComponent`), modelled on the designer-made pages in
`Karmasis.Classic.InfraskopeServer` (`ConfigureDlg`, `EsUrlVerifyDlg`) and
`Karmasis.Classic.InfraskopeWebService` (`DatabaseServerDlg`, `MSMQServerDlg`). On a major upgrade
(`OLDPRODUCTS` set) they are skipped and `FolderDlg` goes straight to `VerifyReadyDlg`; a silent
install never shows them and takes the same `MV_*` properties from the command line.

| Page | Inputs | Next-button checks |
| --- | --- | --- |
| `SqlDlg` | Laid out like SSMS's connect dialog: *Server name* (`MV_SQL_SERVER`), *Database* (`MV_SQL_DATABASE`, default `MiniVault`), *Authentication* radio group (`MV_SQL_AUTH` = `windows` / `sql`), *Login* / *Password* (`MV_SQL_USER`, `MV_SQL_PASSWORD`, enabled for `sql`), *Encrypt connection* (`MV_SQL_ENCRYPT`, default on), *Trust server certificate* (`MV_SQL_TRUSTCERT`). `BuildConnectionString` composes `MV_CONNECTIONSTRING` from them whenever a radio/check box changes, on *Test connection* and on *Next*; the composed string is shown read-only at the bottom. *Enter the connection string directly (advanced)* (`MV_SQL_ADVANCED`) disables the fields and makes that edit writable instead. *Test connection* then runs `TestSqlConnection` and shows `MV_SQL_RESULT` in a message box. | server name (or, in advanced mode, the string) not empty; a login for SQL Server Authentication |
| `ServiceDlg` | *Service account* radio group (`MV_SERVICEACCOUNT_KIND` = `LocalSystem` / `NetworkService` / `Custom`); *Account* and *Account password* (`MV_SERVICEACCOUNT`, `MV_SERVICEACCOUNT_PASSWORD`) enabled for `Custom`; check box *Derive the master key from a password* → `MV_MASTERKEY` + confirmation (`MV_MASTERKEY_CONFIRM`). | Next writes `LocalSystem` / `NT AUTHORITY\NetworkService` into `MV_SERVICEACCOUNT` and clears the password for the built-in choices; `Custom` needs an account; with the master-key box ticked the password is required and must match, unticked clears both |
| `TlsDlg` | `MV_URL`, `MV_CERT_THUMBPRINT`; check box *Use a PFX file* → `MV_CERT_PATH`, `MV_CERT_PASSWORD`. | URL not empty; exactly one certificate source (the other mode's properties are cleared on Next, so `MachineConfigWriter` never sees both) |
| `RecoveryDlg` | check box *Split into Shamir shares* bound to `MV_RECOVERY` (ticked = `shamir`, unticked = cleared = single), `MV_SHARES`, `MV_THRESHOLD` (integer edits); acknowledgement check box `MV_RECOVERY_ACK`. | acknowledgement ticked; for shamir, shares and threshold each within 2..255 |
| `VerifyReadyDlg` | adds a summary (service account, URL, certificate, recovery mode) on a first install; the connection string is not repeated because it may hold a password. | — |
| `ExitDialog` | adds the "open `recovery-<timestamp>.txt`, store it offline, delete it, grant the SQL login" note on a first install. | — |

On the SQL page *Next* is enabled only after a successful *Test connection* (`MV_SQL_OK = "1"`, a
`ControlCondition` re-applied by the page refresh); changing a radio button or check box resets
`MV_SQL_OK`, and *Next* re-runs the test with the current fields before moving on, so a server name
edited after the test is caught too. Choosing SQL Server Authentication pops up a warning that
Windows Authentication is recommended (the login and password end up in `appsettings.json`). The
test result (`MV_SQL_RESULT`) is shown on the message page with an info or exclamation icon; the
page itself only carries a one-line hint about the Next lock.

Two Windows Installer rules learned from verbose logs of test runs: body text must not be
**Transparent** (attribute `0x10000`) — a transparent `Text` overlapping the repaint region of an
edit makes radio buttons and labels vanish while typing (the theme only uses it over the banner
bitmap; our body texts are `3` / `131075`); and **`SpawnDialog` is silently dropped** by Advanced
Installer's UI engine when the same control also publishes `[AiRefreshDlg]`/`NewDialog` (the log
shows the property being set and then straight on to the next dialog, never a "Dialog created" for
the spawned one). Messages therefore use `NewDialog` both ways instead.

A failed check, and the connection-test result, set `MV_UI_ERROR` and move to a small message page
(`MvSqlMsgDlg`, `MvServiceMsgDlg`, `MvTlsMsgDlg`, `MvRecoveryMsgDlg`: an icon, the text, OK) whose OK
button comes back with `NewDialog` to the page it belongs to; the forward `NewDialog` carries the
complementary condition. Coming back re-creates the page, which is also how the Next lock and the
Enable/Disable rules get re-applied — MSI evaluates `ControlCondition` rows only when a page is
built, so the check boxes and radio groups raise `[AiRefreshDlg]=1` (compiled by Advanced Installer
into a `NewDialog` to a generated `<Dialog>_1` clone) for the same effect. `MvSqlMsgDlg` shows the
info icon or the exclamation icon according to `MV_UI_KIND` (`info` after a successful test).

`threshold <= shares` is the one rule the dialog cannot express (MSI conditions compare two
properties as strings), so `ValidateProperties` — immediate, before `InstallInitialize` — checks the
recovery trio with the same rule `MiniVaultCli.BuildInitArguments` applies. That also covers silent
installs, which never see the page.

UI-only properties: `MV_MASTERKEY_USEPASSWORD`, `MV_MASTERKEY_CONFIRM` (hidden from the log like
`MV_MASTERKEY`), `MV_CERT_USEPFX`, `MV_RECOVERY_ACK`, `MV_UI_ERROR`. None of them reaches a custom
action; the existing `MV_*` contract is unchanged.

### What is verified, and what the first designer session still has to check

Verified from the built MSI's tables (Windows Installer API, `verify-aip.ps1 -Build` output):

- **Sequence override.** In the package, `FolderDlg.Next` carries only our two `NewDialog` rows
  (`SqlDlg` when `NOT OLDPRODUCTS`, `VerifyReadyDlg` when `OLDPRODUCTS`, orderings 202/203) plus
  the theme's `SetTargetPath`; the fragment's default `FolderDlg.Next → VerifyReadyDlg` is not there.
  Same for `VerifyReadyDlg.Back`. No double `NewDialog`.
- **`[AiRefreshDlg]`.** Advanced Installer compiles it into `NewDialog <Dialog>_1`, a generated clone
  of the page whose own refresh event points back at the original — that is what the `SqlDlg_1`,
  `ServiceDlg_1`, `TlsDlg_1`, `RecoveryDlg_1` rows in the `Dialog` table are. The check boxes and the
  test button therefore do rebuild the page, and the Enable/Disable conditions are re-evaluated.
- **Placement.** `VerifyReadyDlg`'s theme text ends at y=110; our summary rows start at y=115.
  `ExitDialog`'s theme `Description` occupies y=95..115; the recovery note starts at y=120 (it was
  moved down after the first build showed the overlap).
- **`LockPermissions`** has the two SID rows with `FILE_ALL_ACCESS`; `CheckBox` has the four rows.

Still to check when someone opens the project in the designer or runs the MSI:

1. **Looks.** Text heights were chosen by line count; a wrapped sentence may need a taller control.
   Run the MSI (or open the page in the designer's preview) and read each page once.
2. **Integer edits.** `MV_SHARES` / `MV_THRESHOLD` use the Integer attribute (19); the range
   conditions compare a property with an integer literal, which MSI evaluates numerically. Confirm
   that a non-numeric entry is refused by the control itself.
3. `ARPPRODUCTICON` from `img/ds-48.ico` (committed, not yet referenced) and MiniVault artwork in
   place of the Collector bitmaps.
4. A real install on a test VM: the pre-production checklist in `docs/operations.md`.

There is no file-browse button for the PFX path: the classic theme has no reusable file dialog and a
browse control would need `AI_` custom actions that this project does not ship. The path is typed.

## Tests

`Karmasis.MiniVault.CustomActions.Tests` covers the parts that are pure logic:

* `MachineConfigWriter` — the JSON for both certificate modes, the exact key names the server
  reads, escaping of `\`, `"` and control characters, thumbprint normalization, and the
  "exactly one certificate mode" rules.
* `ProcessRunner.Quote` — `CommandLineToArgvW` quoting, including trailing backslashes.
* `MiniVaultCli.BuildInitArguments` — `single`, `shamir`, `--master-key`, and the argument
  validation.
* `InstallActions` — `WriteMachineConfig` writing and protecting the folder, `RunInit` argument
  building and error reporting through a fake process runner, `ValidateProperties` (double quotes,
  and the `MV_RECOVERY` / `MV_SHARES` / `MV_THRESHOLD` rule including `threshold <= shares`), and
  `TestSqlConnection` against an unreachable server.

The dialogs themselves have no unit tests; `verify-aip.ps1` section 6 checks the rows statically
(declared dialogs and chrome, unique control keys, bound properties declared, password edits hidden,
check boxes in the CheckBox table, events resolving to known dialogs/actions, the page-to-page
navigation both ways, and the test-button event sequence).

They run against a `FakeMsiSession` implementing the kit's `IMsiSession`, so no MSI session handle
is needed.
