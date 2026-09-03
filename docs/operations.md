# MiniVault operations

This page covers installation, the operator commands, TLS, backup/restore, upgrading, troubleshooting,
and the pre-production checklist.

## Quick reference

### Configuration keys

Every key can be set in `appsettings.json`, as an environment variable (`Tls__Url`), or as a
`--Section:Key value` command-line override.

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:MiniVault` | `Server=(localdb)\MSSQLLocalDB;Database=MiniVault;Integrated Security=true;TrustServerCertificate=true` | The MiniVault database. The shipped default is a developer LocalDB; every real install replaces it. |
| `MasterKey:Provider` | `Dpapi` | `Dpapi` (Windows file) or `Environment` (container). |
| `MasterKey:Path` | `%ProgramData%\MiniVault\masterkey.bin` | `Dpapi` only: where the protected key file lives. |
| `Tls:Url` | `https://0.0.0.0:8200` | The single endpoint Kestrel binds. Must be `https://` with an IP-literal host. |
| `Tls:Certificate:Path` | `null` | PFX file holding the server certificate. |
| `Tls:Certificate:Password` | `null` | Password for that PFX. Never logged. |
| `Tls:Certificate:Thumbprint` | `null` | SHA-1 thumbprint of a store certificate instead. Exactly one of `Path` or `Thumbprint`. |
| `Tls:Certificate:StoreName` | `My` | Store to search for `Thumbprint`. |
| `Tls:Certificate:StoreLocation` | `LocalMachine` | `LocalMachine` or `CurrentUser`. |
| `Tls:AllowDevelopmentCertificate` | `false` | Use the ASP.NET Core development certificate. Development environment only. |
| `Tls:AllowDevelopmentCertificateOutsideDevelopment` | `false` | Lets the previous key work outside Development. Automated test hosts only. |
| `Token:LifetimeMinutes` | `15` | Access-token lifetime. |
| `Token:LoginRateLimitPerMinute` | `30` | Requests a minute accepted on `/v1/auth/token`, per server process. |

`Kestrel:Endpoints` and `Kestrel:EndpointDefaults` are rejected at startup, and `ASPNETCORE_URLS`,
`--urls`, `ASPNETCORE_HTTP_PORTS` and `ASPNETCORE_PREFERHOSTINGURLS` are ignored: `Tls:Url` is the
only listener.

Three environment variables are read directly rather than as configuration keys:
`MINIVAULT__MASTERKEY` (the master key itself, with `MasterKey:Provider=Environment`),
`MINIVAULT_INIT_MASTER_KEY` (the password `init --master-key-from-env` derives the key from), and
`ASPNETCORE_ENVIRONMENT` (`Development` unlocks `Tls:AllowDevelopmentCertificate`).

### Commands

| Command | What it does |
|---|---|
| `minivault init --recovery single\|shamir` | Creates the schema, master key, recovery material and first data key. Runs once. |
| `minivault recover --new-master-key <pw\|auto>` | Replaces the master key from the recovery key or shares; rewraps every data key. |
| `minivault rotate-dek` | Creates a new active data key. Needs a service restart afterwards. |
| `minivault migrate` | Applies pending schema migrations. Run after an upgrade, before starting the service. |
| `minivault client add\|remove\|assign\|enable\|disable\|list` | Manages client identities and their role assignments. |
| `minivault role add\|remove\|grant\|list` | Manages roles and their scope rules. |
| `minivault` (no command) | Starts the server. |

## How the keys fit together

- **Master key (KEK)** — 32 random bytes. Lives only on the MiniVault host: a DPAPI-protected file on Windows, the `MINIVAULT__MASTERKEY` environment variable in a container. It never goes into the database.
- **Data keys (DEK)** — encrypt the secret values. Each DEK is stored in the database twice: wrapped by the master key, and wrapped by the recovery key.
- **Recovery key** — shown once by `init`. Lets you replace a lost or forgotten master key. Keep it offline; in Shamir mode split it between people.

Losing both the master key and the recovery material means the secrets are gone. There is no back door.

## Installation

There are three ways to install MiniVault: the Windows MSI, the `install.ps1` script, and a Docker
container. All three end up running the same `minivault.exe`/`minivault` binary and writing the same
configuration shape; pick whichever fits how the target host is managed.

### Windows (MSI)

The MSI installs the server as a Windows service in one step. It is configured entirely through MSI
properties (`MV_*`):

| Property | Default | Meaning |
|---|---|---|
| `MV_CONNECTIONSTRING` | *(empty, required)* | `ConnectionStrings:MiniVault`. |
| `MV_SERVICEACCOUNT` | `LocalSystem` | Account the service runs as; also the identity granted read access to `%ProgramData%\MiniVault`. |
| `MV_SERVICEACCOUNT_PASSWORD` | *(empty)* | Password for `MV_SERVICEACCOUNT`. Leave empty for the built-in accounts (`LocalSystem`, `NT AUTHORITY\NetworkService`, `NT AUTHORITY\LocalService`). The MSI does **not** grant `SeServiceLogonRight` ("Log on as a service") to a non-built-in account — a domain or local service account must already have it **before** the install (grant it in `secpol.msc`, or run `deploy/windows/install.ps1` once, which grants it), otherwise `sc.exe`/the service-install custom action fails and the MSI rolls back with error 1920 (wrapping SCM error 1069, "The service did not start due to a logon failure"). |
| `MV_RECOVERY` | `single` | `single` or `shamir`. |
| `MV_SHARES` | `3` | Shamir shares (>= 2, <= 255). Ignored for `single`. |
| `MV_THRESHOLD` | `2` | Shamir threshold (>= 2, <= shares). Ignored for `single`. |
| `MV_MASTERKEY` | *(empty)* | Optional: derive the master key from this password instead of generating one. |
| `MV_CERT_PATH` | *(empty)* | PFX path. Exactly one of this or `MV_CERT_THUMBPRINT`. |
| `MV_CERT_PASSWORD` | *(empty)* | PFX password. Stored in plain text in `appsettings.json`. |
| `MV_CERT_THUMBPRINT` | *(empty)* | SHA-1 thumbprint of a certificate in `LocalMachine\My`. |
| `MV_URL` | `https://0.0.0.0:8200` | `Tls:Url`. |
| `MV_RECONFIGURE` | *(empty)* | `1` to overwrite an existing `%ProgramData%\MiniVault\appsettings.json`. Empty (the default) keeps it — see "On upgrade" below. |

None of `MV_CONNECTIONSTRING`, `MV_CERT_PASSWORD`, `MV_MASTERKEY` or `MV_SERVICEACCOUNT_PASSWORD` may
contain a double quote (`"`). The installer hands them to its deferred actions as a
`NAME="value"` list, where a quote would silently truncate the value; an immediate `ValidateProperties`
action rejects it up front, naming the property, before anything is installed. If a value inside
`MV_CONNECTIONSTRING` itself needs to contain `;` or `=` (for example inside `Application Name=...`),
use single quotes inside the connection string instead of double quotes — SqlClient accepts
single-quoted values in a connection string, and a single quote survives the `NAME="value"` list.
`install.ps1` is less strict here: it rejects a double quote only in `-MasterKeyPassword`,
`-CertificatePassword` and `-ServiceAccountPassword` (see below) — `-ConnectionString` may contain one,
because it is written straight into `appsettings.json`, where a double quote is just another JSON
character, not a delimiter. The MSI has no such exception: it rejects a double quote in all four
properties (`MV_CONNECTIONSTRING`, `MV_CERT_PASSWORD`, `MV_MASTERKEY`, `MV_SERVICEACCOUNT_PASSWORD`)
alike, because all four travel through the same `NAME="value"` list.

**Interactive install**: on a first install the wizard asks for the same values on four pages —
SQL Server connection (with a *Test connection* button), service account and optional master-key
password, HTTPS certificate (store thumbprint or PFX file) and listen URL, recovery mode (single key
or Shamir shares) with an acknowledgement that the recovery file will be copied and deleted. Each
page validates on *Next* with the rules `WriteMachineConfig` and `RunInit` apply. The recovery
material is still written to a file, not shown on screen (see below), and the finish page says so.
On an upgrade the pages are skipped. The MSI builds from the command line and its dialog tables have
been inspected, but it has not been installed or looked at on any machine yet
(`setups/AdvancedInstaller/README.md`, "Dialogs"); the silent install through `install.ps1` is the
path that has been exercised.

**Silent install**:

```powershell
msiexec /i Karmasis.MiniVault.msi /qn /l*v minivault-install.log `
  MV_CONNECTIONSTRING="Server=sql01;Database=MiniVault;Integrated Security=true" `
  MV_RECOVERY=shamir MV_SHARES=3 MV_THRESHOLD=2 `
  MV_CERT_THUMBPRINT=0123456789ABCDEF0123456789ABCDEF01234567 `
  MV_URL=https://0.0.0.0:8200
```

(PowerShell continues a line with a backtick; from `cmd.exe` use `^` instead.)

**What the installer does**, in order:

1. Installs the publish output to `[ProgramFiles64Folder]Karmasis\MiniVault`.
2. Creates `%ProgramData%\MiniVault` and grants SYSTEM + Administrators full control.
3. Writes `%ProgramData%\MiniVault\appsettings.json` from the `MV_*` properties and re-applies the
   protected ACL (the same grants `install.ps1` applies with `icacls`).
4. Runs `minivault.exe init --recovery <mode> --out %ProgramData%\MiniVault\recovery-<timestamp>.txt`.
5. Registers the `KarmasisMiniVault` service (running as `MV_SERVICEACCOUNT`, with
   `MV_SERVICEACCOUNT_PASSWORD` when that account needs one) **and starts it**. A successful
   installation therefore leaves a running service, not just a registered one.
6. (Not sequenced into the install; run by the *Test connection* button on the SQL page.) Tests the
   connection string in `MV_CONNECTIONSTRING` and reports back via `MV_SQL_OK`/`MV_SQL_ERROR`. A
   database that does not exist yet passes with a note — `init` creates it — as long as the login
   may create databases; "the database exists but this login cannot open it" and "the login cannot
   create databases" are reported as failures with those words.

The recovery material is written to `%ProgramData%\MiniVault\recovery-<timestamp>.txt` (a deferred
custom action cannot show it in the UI). **Open that file, copy the recovery key or shares to a safe
offline location, and delete it** — it is not shown again and is readable only by SYSTEM and
Administrators.

**Secrets and `/l*v` logs**: `MV_CONNECTIONSTRING`, `MV_CERT_PASSWORD`, `MV_MASTERKEY` and
`MV_SERVICEACCOUNT_PASSWORD` are listed in `MsiHiddenProperties`, and so are the deferred action names
`WriteMachineConfig` and `RunInit` — a deferred action reads its input from a property named after the
action, and MSI logs that property like any other, so hiding only the `MV_*` properties would still
leave full copies of every secret in the log. **A verbose log taken with an MSI built before this fix
does contain those secrets in clear text**: treat any existing `minivault-install.log` as a secret,
and rotate the connection-string password, PFX password and master key if such a log was shared.

**On upgrade**:

- Step 4 (`init`) is skipped. The installer treats the server's "already initialized" response as a
  no-op instead of failing the upgrade, so re-running or upgrading the MSI over an existing install
  does not touch the existing vault, master key, or data.
- Step 3 keeps the existing `%ProgramData%\MiniVault\appsettings.json` — an upgrade started from
  Add/Remove Programs supplies the *default* `MV_*` values, and writing those over a working
  configuration would take the server down. Pass `MV_RECONFIGURE=1` when you really do want the
  configuration rewritten from the `MV_*` properties. The protected ACL is re-applied either way.
- The service is stopped before its files are replaced and started again afterwards (`ServiceControl`
  event `163`).

See "Upgrading" below for what you still need to run by hand (`minivault migrate`).

### Windows (script)

`install.ps1` does the same work from PowerShell, for hosts that are provisioned by script rather
than MSI. Publish first:

```powershell
dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64
```

Then, with a certificate from a PFX file:

```powershell
.\install.ps1 `
  -SourceDir C:\publish\minivault `
  -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" `
  -CertificatePath C:\certs\minivault.pfx `
  -CertificatePassword "the-pfx-password"
```

Or with a certificate already imported into a machine store:

```powershell
.\install.ps1 `
  -SourceDir C:\publish\minivault `
  -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" `
  -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

**Re-running it upgrades in place.** When the service already exists the script stops it before Step 1
(`robocopy /MIR` cannot replace a running executable), and Step 5 reconfigures it with `sc.exe config`
instead of creating it. `-WhatIfMode` says which of the two will happen
(`(service exists -> stop/config)`). The same command line therefore installs and upgrades.

For a strictly ordered rollout — grant the SQL login before the service ever tries to start — pass
`-SkipServiceStart`. The script prints the SQL grant script right before it would otherwise start the
service; run that on the target SQL Server, then start the service yourself:

```powershell
.\install.ps1 -SourceDir C:\publish\minivault -ConnectionString "..." -CertificateThumbprint ... -SkipServiceStart
# apply the printed SQL grant on the target SQL Server, then:
sc.exe start KarmasisMiniVault
```

`-SkipInit` skips vault creation entirely (files, config, ACLs and the service are still installed);
it is for restoring an existing vault onto a new host rather than creating one — see "Backup and
restore" below. `-NonInteractive` replaces the "type SAVED to continue" prompt after `init` with a
warning, for unattended provisioning; the recovery material still has to be collected from the
script's output.

**Least-privilege SQL grants.** The script prints two scripts, not one. The *running service* only
reads and writes rows, so it needs `db_datareader` + `db_datawriter` and nothing more:

```sql
CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
CREATE USER  [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
ALTER ROLE db_datareader ADD MEMBER [NT AUTHORITY\SYSTEM];
ALTER ROLE db_datawriter ADD MEMBER [NT AUTHORITY\SYSTEM];
```

Schema changes belong to the *operator* who runs `minivault init` / `minivault migrate`, not to the
service: give that account `db_ddladmin` (enough for `migrate` on an existing schema) or `db_owner`
(needed the first time, when `init` creates the schema), and revoke it again afterwards if your policy
requires least privilege at rest.

**Exit codes.** `0` success, `1` bad input or a failed step, `2` the service was installed and started
but `https://localhost:<port>/v1/health` did not answer within 30 seconds. Exit 2 usually means the SQL
grant has not been applied yet, the certificate is wrong, or the vault is not initialized — check the
Windows Application event log. Pass `-IgnoreHealthCheck` to downgrade it to a warning (exit 0), for
example when the grant is applied later in the rollout.

**Service account rights.** A `-ServiceAccount` that is not one of the built-in identities needs
`SeServiceLogonRight` ("Log on as a service"), or the Service Control Manager refuses to start it with
error **1069**. The script grants it with `secedit` after creating or reconfiguring the service; pass
`-SkipLogonRightGrant` when Group Policy already manages that right (a local grant would be overwritten
at the next policy refresh anyway).

**Passwords never go on a command line where that can be avoided.** `-MasterKeyPassword` is handed to
`minivault.exe init` through the `MINIVAULT_INIT_MASTER_KEY` environment variable (`--master-key-from-env`),
and a *new* service with `-ServiceAccountPassword` is created with `New-Service -Credential`, which
passes the password through the Win32 API. Reconfiguring an *existing* service is the one exception:
`sc.exe config password= ...` is the only way Windows offers, so the password does appear on that
command line — visible to anything that can list processes, and to command-line auditing (Event ID
4688). The script prints a warning when it does this. `-MasterKeyPassword`, `-CertificatePassword` and
`-ServiceAccountPassword` are rejected up front if they contain a double quote, which cannot survive
the re-quoting on the way to a child process.

Uninstall:

```powershell
.\uninstall.ps1 -ServiceName KarmasisMiniVault -InstallDir "C:\Program Files\Karmasis\MiniVault"
```

This removes the service and the install directory but leaves `%ProgramData%\MiniVault` (the master
key and machine config) in place. Add `-PurgeData -Force` only when the vault and its database are
also being decommissioned — see `deploy/windows/README.md` for the full parameter list and the
`-WhatIfMode` preview flag.

### Docker

Build the image, generate a certificate, initialize, then run:

```powershell
# Build (local dev — stages nupkgs from the local-nuget folder feed):
.\docker\build-local.ps1
# CI builds instead pass a private-feed NuGet config as a build arg:
#   docker build -f docker/Dockerfile --build-arg NUGET_CONFIG=nuget-dev.config -t karmasis/minivault:ci .

# Generate a self-signed PFX for local/dev use (PowerShell). Note: not $pwd - that is PowerShell's
# built-in alias for the current directory, and assigning to it breaks Get-Location.
$pfxPassword = ConvertTo-SecureString -String "change-me" -Force -AsPlainText
$cert = New-SelfSignedCertificate -DnsName "localhost" -CertStoreLocation "cert:\CurrentUser\My" -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(2)
New-Item -ItemType Directory -Force -Path .\docker\certs | Out-Null
Export-PfxCertificate -Cert $cert -FilePath .\docker\certs\minivault.pfx -Password $pfxPassword | Out-Null
Remove-Item "cert:\CurrentUser\My\$($cert.Thumbprint)"

# Initialize (one-shot job, profile "init"):
docker compose -f docker/docker-compose.yml --env-file docker/.env --profile init run --rm minivault-init

# Run:
docker compose -f docker/docker-compose.yml --env-file docker/.env up -d minivault

# Health:
curl -k https://localhost:8200/v1/health

# Logs:
docker compose -f docker/docker-compose.yml logs -f minivault
```

Notes:

- The container runs as a non-root user (uid 1654). Give the mounted PFX to that uid rather than to
  everyone: `chown 1654:1654 docker/certs/minivault.pfx && chmod 640 docker/certs/minivault.pfx`. A
  world-readable `644` also works, but the PFX password is in `docker/.env` next to it, so the pair is
  worth keeping off other local accounts.
- `minivault-init`'s stdout is the only place the master key and recovery material appear (with
  `MasterKey__Provider=Environment`, which cannot store the key itself). Copy the master key into
  `docker/.env` as `MINIVAULT__MASTERKEY` and the recovery key/shares into your own safe storage.
  `docker compose run --rm` already deletes the init container, so there is no container log left to
  clear — the value survives only in your terminal scrollback and in whatever your shell records.
- **`MINIVAULT__MASTERKEY` is an environment variable of the running container, so
  `docker inspect minivault` prints it in clear text**, as does anything that reads
  `/proc/<pid>/environ` on the host. Everyone with access to the Docker socket therefore has the
  master key. That is inherent to `MasterKey:Provider=Environment`; keep the socket restricted, and
  prefer a real secret store (or the Windows DPAPI provider) for anything beyond a single-tenant
  host.
- Both services declare `extra_hosts: ["host.docker.internal:host-gateway"]` so a host SQL Server is
  reachable as `host.docker.internal` from a plain Linux Docker engine (a no-op, but harmless, on
  Docker Desktop, where that name already resolves).
- `docker/.env` (holding the connection string, master key and PFX password) and `docker/certs/` are
  git-ignored; never commit either.

See `docker/README.md` for the full walkthrough, including the CI restore caveat.

## Commands

All commands read the same configuration as the server: `appsettings.json` next to the binary, then `%ProgramData%\MiniVault\appsettings.json` (Windows), then environment variables, then command-line overrides of the form `--Section:Key value` (for example `--ConnectionStrings:MiniVault "..."`). Any other unknown option is rejected.

### `minivault init`

Creates the schema, the master key, the recovery material and the first data key. Refuses to run on an initialized database.

```
minivault init --recovery single
minivault init --recovery shamir --shares 3 --threshold 2
minivault init --recovery shamir --shares 5 --threshold 3 --master-key "my passphrase" --out recovery.txt
```

| Option | Meaning |
|---|---|
| `--recovery single\|shamir` | One recovery key, or `shares` Shamir shares of which any `threshold` recover. |
| `--shares n --threshold k` | Shamir only. `2 ≤ k ≤ n ≤ 255`. Recommended minimum: 3 shares, threshold 2. |
| `--master-key <password>` | Derive the master key from a password (PBKDF2, salt and iteration count are stored in the database). Without it a random key is generated. The password is used only to derive the key at this moment; it is **not** a way back in later — if the master key file or environment value is lost, only the recovery material helps. **Interactive use only:** the password is on the command line, so anything that can list processes — and command-line auditing (Event ID 4688) — can read it. |
| `--master-key-from-env` | Same as `--master-key`, but the password is read from the `MINIVAULT_INIT_MASTER_KEY` environment variable and removed from the process's environment as soon as it has been read, so it never reaches a command line. This is what `install.ps1` and the MSI use. Mutually exclusive with `--master-key`. |
| `--out <file>` | Also write the output to a file. The file is created with permissions for the current user only and is never overwritten (`init` fails if it already exists). Delete it once the material is stored safely. |
| `--force` | Overwrite a master key that already exists in the provider. Without it, `init` refuses so that another vault on the same host does not lose its key. |

Output example:

```
MiniVault initialized.
Recovery mode: shamir (2 of 3)

Store the following recovery material offline, in separate places. It is shown only once and is not saved anywhere.
Share 1: AQ...
Share 2: Ag...
Share 3: Aw...

Master key stored by the Dpapi provider.
```

With the `Environment` provider the last line instead reads
`Master key (set as MINIVAULT__MASTERKEY before starting the server): <base64>`, because that
provider cannot store the key itself. That means the master key goes to standard output — in Docker,
to `docker logs`; copy the value into your own configuration and clear or rotate the log.

### `minivault recover`

Replaces the master key using the recovery material. Every data key is rewrapped under the new master key; secrets are not touched. Use it when the master key is lost, or simply to change it.

```
minivault recover --new-master-key auto --recovery-key <key>
minivault recover --new-master-key "new passphrase" --share <share1> --share <share3>
```

`auto` generates a random master key. Any `threshold` shares work, in any order. Give exactly one of
`--recovery-key` or one or more `--share`; both, or neither, is a parse error.

```
Master key replaced. Data keys rewrapped: 2.
Master key stored by the Dpapi provider.
```

If the rewrap succeeds but the provider cannot store the new key, the command fails with a
`VaultException` whose message carries that key in base64 — place it by hand. The recovery material
stays valid either way, because `WrappedByRecovery` is never touched.

### `minivault rotate-dek`

Creates a new active data key. New and updated secrets use it; existing secrets stay readable with their old key. Needs the master key (it unwraps the stored recovery key to wrap the new data key).

```
minivault rotate-dek
```

```
Active data key version: 2
```

Restart the MiniVault service after rotating; the running server loads data keys at startup and will not see the new version until it restarts.

### `minivault migrate`

Applies any pending database schema migrations. Run it after upgrading the binaries, before starting
the service, so the schema matches the new code — `init` is the only other place migrations run, and
it only runs once. `migrate` is safe to run on a database that has not been initialized yet (it just
creates the schema) and safe to run repeatedly: a second run with nothing pending is a no-op.

```
minivault migrate
```

```
Applied 1 migration(s).
```

or, when nothing was pending:

```
Database is up to date.
```

### `minivault` (no command)

Starts the server. There is no `serve` subcommand: anything that is not one of the commands above
starts the server. It refuses to start when the vault is not initialized or the master key does not
unwrap the data keys; the reason is written to the log (see "Troubleshooting").

## Clients and roles

Services that call MiniVault authenticate as **clients**. A client has an id, a secret, and zero or more **roles**.

A role is just a name plus a list of rules. Each rule is a scope prefix and a permission (`read`, or `write` which includes `read`). A client can read or write a secret if any of its roles has a rule whose scope is a prefix of the secret's name. A role with no rules grants nothing. End scopes with `/` — `dataskope` would also match `dataskope-other/...` because matching is by prefix.

### `minivault role add <name> [--description "..."]`

Creates a role.

```
Role created: collector-reader
```

### `minivault role remove <name>`

Deletes a role, its rules, and its assignment to any client.

```
Role removed: collector-reader
```

### `minivault role grant <name> --scope <prefix> --permission read|write`

Grants a permission on a scope to a role. Granting again on the same scope replaces the existing rule (it does not add a second one).

```
Granted Read on 'dataskope/collector/' to collector-reader
```

A scope is up to 256 characters of letters, digits, `.`, `_` and `-` in `/`-separated segments; anything else is rejected. The empty scope covers **every** secret in the vault, so it cannot be reached by an empty `--scope` (a shell that expands an unset variable away would grant it by accident) — ask for it explicitly:

```
minivault role grant break-glass --all --permission write
```

### `minivault role list`

Lists every role and its rules, one line per role.

```
collector-reader: dataskope/collector/=Read
empty-role: (no rules)
```

### `minivault client add <id> [--role <r> ...]`

Creates a client and prints its secret. `--role` can be repeated to assign roles at creation time.

```
Client created: dataskope-collector
Client secret: 8k3F2v9qA1zR7pC0eQ6nS4gU2y5T0hJ3W8lD1bXfM6o=
Store this secret now; it is not shown again.
```

The secret is only ever shown here. Store it in the consuming service's own secret storage — on Windows, protect it with DPAPI before it touches disk.

### `minivault client remove <id>`

Deletes a client. It can no longer authenticate; any token it already holds still works until it expires (15 minutes by default).

```
Client removed: dataskope-collector
```

### `minivault client assign <id> --role <r>`

Assigns an existing role to an existing client. Assigning a role the client already has is a no-op.

```
Assigned role collector-reader to dataskope-collector
```

### `minivault client disable <id>` / `minivault client enable <id>`

Turns a client off without deleting it, and back on. A disabled client cannot obtain new tokens; a token it already holds keeps working until it expires (15 minutes by default). Use `disable` for a suspected compromise, `remove` when the client is gone for good.

```
Client disabled: dataskope-collector
Client enabled: dataskope-collector
```

### `minivault client list`

Lists every client, whether it is enabled, and its roles.

```
dataskope-collector [enabled]: collector-reader
other-client [disabled]: (no roles)
```

### Example: onboarding a new client

```
minivault role add collector-reader --description "reads collector secrets"
minivault role grant collector-reader --scope dataskope/collector/ --permission read
minivault client add dataskope-collector --role collector-reader
```

The last command prints the client's secret once. Copy it into the consuming service's configuration immediately; MiniVault does not store or display it again.

### Audit trail

Every command above writes an audit row with client id `cli`. The action names are `client.add`, `client.remove`, `client.assign`, `client.enable`, `client.disable`, `role.add`, `role.remove`, `role.grant`. The other operator commands use the same client id and the action names `init`, `recover`, `rotate-dek` and `migrate`. `client list` and `role list` read nothing and write no audit row.

## Master key providers

| `MasterKey:Provider` | Where the key lives | Notes |
|---|---|---|
| `Dpapi` (default) | `%ProgramData%\MiniVault\masterkey.bin`, DPAPI LocalMachine | Windows only — the provider throws `PlatformNotSupportedException` elsewhere. Bound to the machine: the file cannot be read on another host. `MasterKey:Path` overrides the location. |
| `Environment` | `MINIVAULT__MASTERKEY` (base64, 32 bytes) | Containers / Linux. Cannot store a key, so `init` and `recover` print the value once instead. |

## Backup and restore

Back up two things, separately: the database (normal SQL Server backup) and the recovery material.
Neither is useful alone — the database holds secrets encrypted by data keys that are themselves
wrapped by the recovery key, and the recovery key exists only in whatever offline location you put it
when `init` printed it.

**Do not try to back up the master key file** (`%ProgramData%\MiniVault\masterkey.bin`, DPAPI). It is
protected with `CurrentMachine`-scoped DPAPI, so a copy of the file is unreadable on any host other
than the one it was created on — restoring it to a new machine does not work, by design. The recovery
material is what stands in for it when moving to a new host: `minivault recover` builds a fresh master
key from the recovery key or Shamir shares and rewraps the existing data keys with it.

### Restoring onto a new host

1. **Install, without creating a new vault.** Run `install.ps1` with `-SkipInit` (files, config, ACLs
   and the service are installed; no `init` runs):

   ```powershell
   .\install.ps1 -SourceDir C:\publish\minivault -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" -CertificateThumbprint ... -SkipInit -SkipServiceStart
   ```

   `-SkipServiceStart` keeps the service stopped until the database is actually in place (step 2) and
   the vault has a master key again (step 3) — starting it any earlier just fails the startup check.
2. **Restore the database backup** onto the target SQL Server (or attach it), pointed at by the same
   `ConnectionStrings:MiniVault` the install step configured.
3. **Recover the master key** using the recovery material saved when the vault was first initialized:

   ```powershell
   minivault.exe recover --new-master-key auto --share <share1> --share <share2>
   # or, for single-key recovery mode:
   minivault.exe recover --new-master-key auto --recovery-key <key>
   ```

   This stores a brand-new master key on the new host (DPAPI on Windows) and rewraps every data key
   with it; secrets themselves are untouched.
4. **Start the service** (`sc.exe start KarmasisMiniVault`, or start it normally if you did not pass
   `-SkipServiceStart`) and apply the SQL grant script printed by `install.ps1` first, if you have not
   already.
5. **Verify** `https://<host>/v1/health` returns `{"status":"ok","initialized":true,...}`.

### Container variant

```bash
docker run --rm \
  -e ConnectionStrings__MiniVault="Server=...;Database=MiniVault;..." \
  -e MasterKey__Provider=Environment \
  karmasis/minivault:dev recover --new-master-key auto --share <share1> --share <share2>
```

Copy the printed master key into `docker/.env` as `MINIVAULT__MASTERKEY` (the `Environment` provider
cannot store it itself), then start the `minivault` service normally.

## TLS

The server listens on HTTPS only; there is no plain-HTTP endpoint. Configuration lives under `Tls`:

| Key | Default | Notes |
|---|---|---|
| `Tls:Url` | `https://0.0.0.0:8200` | The single endpoint Kestrel binds. The host must be an IP literal — `0.0.0.0`, `::`, `localhost`, or a specific address — not a DNS name. `ASPNETCORE_URLS`, `--urls`, `ASPNETCORE_PREFERHOSTINGURLS` and `ASPNETCORE_HTTP_PORTS` are **ignored** (a warning is logged for `ASPNETCORE_URLS`, and `PreferHostingUrls` is pinned to `false` so none of them can override the explicit `Listen` call), and `Kestrel:Endpoints`/`Kestrel:EndpointDefaults` are **rejected**: startup fails with `Kestrel:Endpoints is not supported: MiniVault listens only on Tls:Url over HTTPS.` rather than silently ignoring configuration that an operator expects to add a listener — most dangerously a plain-HTTP one; the server also refuses to start if any non-HTTPS address is bound. |
| `Tls:Certificate:Path` / `Tls:Certificate:Password` | `null` | Load the server certificate from a PFX file. |
| `Tls:Certificate:Thumbprint` | `null` | Load the server certificate (with its private key) from a certificate store instead of a file. |
| `Tls:Certificate:StoreName` / `Tls:Certificate:StoreLocation` | `My` / `LocalMachine` | Where to look up `Thumbprint`. `StoreLocation` is `LocalMachine` or `CurrentUser`. |
| `Tls:AllowDevelopmentCertificate` | `false` | Development only: use Kestrel's ASP.NET Core HTTPS development certificate instead of a configured one. Startup fails outside the Development environment unless `Tls:AllowDevelopmentCertificateOutsideDevelopment` is also `true`. |
| `Tls:AllowDevelopmentCertificateOutsideDevelopment` | `false` | Allows `Tls:AllowDevelopmentCertificate` to be used outside Development. For automated test hosts only — never set this for a real deployment. |

Set exactly one of `Tls:Certificate:Path` or `Tls:Certificate:Thumbprint` unless `Tls:AllowDevelopmentCertificate` is `true`. A misconfigured certificate (bad path, wrong password, missing thumbprint) fails startup immediately with a critical log entry, before the vault startup check runs.

For local development, trust the ASP.NET Core development certificate once and let the server use it:

```
dotnet dev-certs https --trust
```

`appsettings.Development.json` already sets `Tls:AllowDevelopmentCertificate: true`. For a real install, use a PFX (`Tls:Certificate:Path`/`Password`) or import the certificate into a machine store and reference it by `Tls:Certificate:Thumbprint`.

Self-signed installs: clients that talk to a MiniVault server with a self-signed or otherwise untrusted certificate must pin the certificate's thumbprint (see `docs/client.md`, section 11 "TLS") rather than disabling validation.

### PFX vs. store thumbprint

| | `Tls:Certificate:Path`/`Password` (PFX) | `Tls:Certificate:Thumbprint` (store) |
|---|---|---|
| Where the private key lives | In the PFX file, loaded on every startup. | In the Windows certificate store (`LocalMachine\My` by default); nothing outside the store to protect. |
| Rotation | Replace the file (and update the password if it changed). | Import the new certificate, point `Thumbprint` at it. |
| Secret at rest | The PFX password sits in `appsettings.json` in plain text (ACL-protected, but still plaintext). | No file password to store. |
| Portability | Easy to copy to a container or a second host. | Windows-only; the certificate has to be imported on each host that needs it. |
| Recommended for | Docker/Linux (no certificate store), or when the same PFX is deployed to several hosts. | A dedicated Windows host with its own certificate lifecycle (e.g. imported by a CA enrollment tool). |

Set exactly one of the two; `Tls:AllowDevelopmentCertificate` is the only way to configure neither, and it is Development-only (see below).

### Renewal runbook

1. Get the new certificate (from your CA, or a freshly generated self-signed one for internal use).
2. Either replace the PFX at `Tls:Certificate:Path` (same filename, same or updated password) or
   import the new certificate into the store and update `Tls:Certificate:Thumbprint` to match.
3. Restart the MiniVault service — Kestrel loads the certificate once at startup; a running server
   does not pick up a replaced file or a new thumbprint on its own.
4. **For self-signed installs**, update `ServerCertificateThumbprint` in every client's configuration
   to the new certificate's thumbprint (see `docs/client.md`, section 11). Pinned clients reject the
   new certificate — correctly, since pinning replaces chain validation rather than adding to it —
   until they are updated. A certificate from a trusted CA needs no client-side change.

### Development certificate

`Tls:AllowDevelopmentCertificate` is for local development only: it tells Kestrel to use `dotnet
dev-certs https --trust`'s certificate instead of a configured one. `appsettings.Development.json`
sets it to `true`, which is why it works out of the box in the Development environment. Startup
rejects it everywhere else — `TlsStartupCheck` fails fast with "`Tls:AllowDevelopmentCertificate is
only allowed in the Development environment...`" — unless `Tls:AllowDevelopmentCertificateOutsideDevelopment`
is also `true`. That escape hatch exists for automated test hosts; never set it for a real deployment,
since the ASP.NET Core development certificate is not a secret and is not tied to any specific host.

## HTTP API

| Method | Path | Auth | Success | Error codes |
|---|---|---|---|---|
| `POST` | `/v1/auth/token` | none | 200 | `invalid_request`, `unauthorized` |
| `GET` | `/v1/secrets/{name}` | Bearer | 200 (304 if `If-None-Match` matches) | `invalid_request`, `unauthorized`, `forbidden`, `not_found` |
| `PUT` | `/v1/secrets/{name}` | Bearer | 200 | `invalid_request`, `unauthorized`, `forbidden`, `conflict` |
| `DELETE` | `/v1/secrets/{name}` | Bearer | 204 | `invalid_request`, `unauthorized`, `forbidden`, `not_found` |
| `GET` | `/v1/secrets?prefix=` | Bearer | 200 | `unauthorized`, `forbidden` |
| `GET` | `/v1/health` | none | 200 | — |

Any endpoint can also return `vault_unavailable` (503, the master key or database is temporarily unreachable) or `internal_error` (500, unexpected failure); both are logged server-side.

Response bodies: `POST /v1/auth/token` returns `{"accessToken","expiresIn"}` (`expiresIn` in seconds,
900 by default); `GET /v1/secrets/{name}` returns `{"name","value","contentType","version","updatedAt"}`
with `value` base64-encoded; `PUT` returns `{"version"}`; `GET /v1/secrets?prefix=` returns
`[{"name","version","updatedAt"}]` and never a value; `GET /v1/health` returns
`{"status","initialized","activeDataKeyVersion"}`.

`GET /v1/secrets?prefix=` validates the prefix: at most 256 characters of letters, digits, `.`, `_`, `-` and `/`. Anything else is `invalid_request` (400). An empty prefix is allowed and means "the whole vault", which needs a rule whose scope is the empty scope.

`If-None-Match` on `GET /v1/secrets/{name}` is a proper entity-tag list: `"3"`, `W/"3"` (weak tags compare equal — the vault has one representation per version) and `*` all produce a 304, and the 304 carries the current `ETag` header just as the 200 would.

### Other status codes

These come from the pipeline rather than from an endpoint, and carry the same JSON error shape:

| Status | `error` | When |
|---|---|---|
| `405` | `invalid_request` | The path exists but not for that method, e.g. `POST /v1/secrets/{name}`. |
| `415` | `invalid_request` | The request body is not `application/json`. |
| `429` | (no body) | More than `Token:LoginRateLimitPerMinute` requests a minute reached `/v1/auth/token`. The default is 30, counted per server over a fixed one-minute window; the other endpoints are not rate-limited because they already need a token. |
| `499` | (no body) | The client closed the connection before a response was produced. Nothing is sent; the row exists only in the access log. |

### Audit trail

Every request that reaches an endpoint writes a row: `token`, `secret.read`, `secret.write`, `secret.delete`, `secret.list`. Failed attempts are recorded too, with `Success = 0` and the reason in `Detail`; for `secret.list` the requested prefix is the detail and the secret name is left empty.

A request that is rejected by the bearer-token check never reaches an endpoint, so it is audited separately as **`token.rejected`** with client id `(anonymous)`, the caller's IP, and the token handler's reason (or `missing or invalid bearer token`). Watch this action together with failed `token` rows: both are what credential guessing and token replay look like from the outside.

Audit rows are written on their own database connection, independent of the request's own work, so a failed or rolled-back write still leaves its audit row behind.

### Error codes

| `error` | Meaning |
|---|---|
| `unauthorized` | Missing, invalid, or expired bearer token; or bad credentials at `/v1/auth/token`. |
| `forbidden` | The token's roles have no rule whose scope is a prefix of the requested secret name (or the requested permission is read-only where write is required). |
| `not_found` | No secret exists at that name. |
| `invalid_request` | Malformed input: a secret name that is not 1–256 characters of letters, digits, `.`, `_` and `-` in `/`-separated segments (a segment made only of dots, such as `..`, is rejected too); a missing or non-base64 `value`; missing `clientId`/`clientSecret`; a value over 1,048,576 bytes; a `contentType` over 128 characters; a body that is not readable JSON. |
| `conflict` | The secret was modified concurrently (optimistic concurrency); retry the request. |
| `vault_unavailable` | The vault is temporarily unavailable (master key or database unreachable). |
| `internal_error` | Unexpected server failure. |

### Example: token, write, read with ETag, conditional read

There is no plain-HTTP listener, so every example is `https://`. `-k` is only for a self-signed or
development certificate; drop it against a certificate the host trusts.

```
curl -sk -X POST https://minivault.local:8200/v1/auth/token \
  -H "Content-Type: application/json" \
  -d '{"clientId":"c","clientSecret":"<client secret>"}'
# {"accessToken":"eyJ...","expiresIn":900}

TOKEN=eyJ...

curl -sk -X PUT https://minivault.local:8200/v1/secrets/test/one \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"value":"aGVsbG8=","contentType":"text/plain"}'
# {"version":1}

curl -ski https://minivault.local:8200/v1/secrets/test/one -H "Authorization: Bearer $TOKEN"
# HTTP/1.1 200 OK
# ETag: "1"
# {"name":"test/one","value":"aGVsbG8=","contentType":"text/plain","version":1,"updatedAt":"..."}

curl -ski https://minivault.local:8200/v1/secrets/test/one \
  -H "Authorization: Bearer $TOKEN" -H 'If-None-Match: "1"'
# HTTP/1.1 304 Not Modified
```

From Windows PowerShell 5.1, use `curl.exe` (plain `curl` is an alias for `Invoke-WebRequest`) and
put the JSON body in a file (`-d "@body.json"`): PowerShell 5.1 mangles the double quotes when it
hands a JSON string to a native executable, and the server answers `invalid_request`.

## Upgrading

Migrations run automatically only inside `init`, which runs once. After upgrading the binaries,
apply any pending schema changes yourself with `minivault migrate` before starting the new service.

### Windows

1. Stop the service: `sc.exe stop KarmasisMiniVault`. The MSI and a re-run of `install.ps1` both do
   this for you.
2. Replace the files — run the new MSI (it replaces `APPDIR`, skips `init`, and keeps the existing
   `%ProgramData%\MiniVault\appsettings.json` unless `MV_RECONFIGURE=1`), or re-run `install.ps1`
   with the same arguments (it stops the service, `robocopy /MIR`s the new publish output over the
   install directory, and reconfigures the existing service instead of creating it).
3. Run `minivault.exe migrate` from the install directory. The operator account running it needs DDL
   rights on the database — the service account itself only has `db_datareader`/`db_datawriter`.
4. Start the service: `sc.exe start KarmasisMiniVault`. The MSI does this itself; `install.ps1` does
   too, unless `-SkipServiceStart` was passed.

### Docker

```powershell
docker pull karmasis/minivault:dev   # or rebuild with build-local.ps1 / CI

docker run --rm --env-file docker/.env karmasis/minivault:dev migrate

docker compose -f docker/docker-compose.yml up -d minivault   # picks up the new image
```

`MINIVAULT__MASTERKEY` and the certificate volume are unaffected by an image swap; `migrate` needs
the same connection string and master key as the server, so pass the same `--env-file`.

### Rotating the data key on upgrade

If the upgrade is also a good time to rotate the active data key, run `minivault rotate-dek` — but
remember it needs its own service restart afterwards (see "`minivault rotate-dek`" above): the
running server loads data keys once at startup and does not see a new version until it restarts.

## Troubleshooting

### Startup refusals

The service logs a single critical line — `MiniVault cannot start: <reason>` — and **exits with code
3** when any of these checks fails. No stack trace is printed: the reason is the message. TLS is
checked before the vault, so a certificate problem is reported even when the vault itself is fine.

Where to read that line: `sc.exe query KarmasisMiniVault` for the state, the Windows Application event
log for the message, `docker logs <container>` for a container. `install.ps1` exits `2` (not 3) when it
installed the service successfully but the health endpoint never answered — the service's own exit code
is what says why.

| Message (abbreviated) | Cause | Fix |
|---|---|---|
| `The vault is not initialized. Run 'minivault init' first.` | No `VaultMetadata` row — `init` was never run against this database (or the connection string points at the wrong database). | Run `minivault init`, or check `ConnectionStrings:MiniVault` points at the right database. |
| `Master key unavailable (Dpapi): Master key file not found: ...` | `masterkey.bin` is missing — wrong host, wrong `MasterKey:Path`, or the file was deleted. | Restore it via `minivault recover` with the recovery material (see "Backup and restore"), or fix `MasterKey:Path`. |
| `Master key unavailable (Environment): Environment variable MINIVAULT__MASTERKEY is not set.` | The container/process does not have `MINIVAULT__MASTERKEY` set. | Set it from the value `init`/`recover` printed. |
| `The master key does not unwrap the stored data keys. Wrong master key for this database, or the database belongs to another vault.` | The master key present does not match the one this database's data keys were wrapped with — typically a copied database pointed at a different host's key, or a restored master key that does not match. | Recover with the correct recovery material, or point the connection string at the right database. |
| `Tls:AllowDevelopmentCertificate is only allowed in the Development environment...` | `Tls:AllowDevelopmentCertificate=true` outside `ASPNETCORE_ENVIRONMENT=Development`. | Configure a real `Tls:Certificate:Path`/`Thumbprint`; never set `Tls:AllowDevelopmentCertificateOutsideDevelopment` for a real deployment. |
| `Could not load the TLS certificate from '<path>'. Check that the file exists and that Tls:Certificate:Password is correct.` | Bad PFX path or wrong password. | Fix `Tls:Certificate:Path`/`Password`. |
| `No certificate with thumbprint '<thumb>' and a private key was found in <location>\<store>.` | The thumbprint is not installed in the configured store, or is installed without its private key. | Import the certificate (with its private key) into the configured store, or fix the thumbprint/`StoreLocation`. |
| `Kestrel:Endpoints is not supported: MiniVault listens only on Tls:Url over HTTPS.` | `Kestrel:Endpoints` or `Kestrel:EndpointDefaults` is set in configuration. MiniVault binds one HTTPS endpoint explicitly and would ignore them, so it refuses to start rather than leave you believing an extra (possibly plain-HTTP) listener exists. | Remove the `Kestrel` endpoint configuration and set `Tls:Url` instead. |
| `Database is not reachable. Check ConnectionStrings:MiniVault.` | The SQL connection failed at startup (wrong instance, no network route, no login for the service account). | Check the connection string, that the SQL login exists (see the grants `install.ps1` prints), and that the service account can reach the instance. |
| `Expected exactly one active data key, found <N>.` | Zero or more than one `DataKeys` row has `IsActive = 1` — normally impossible (the schema has a filtered unique index on it) unless the table was edited by hand. | Fix the `DataKeys` table so exactly one row is active; if this happens without manual edits, treat it as a bug and preserve the database for investigation. |

### The service will not start at all

| Symptom | Cause | Fix |
|---|---|---|
| `sc.exe start` fails with **error 1069** ("The service did not start due to a logon failure") | The service account has no `SeServiceLogonRight`, or its password is wrong/expired. | Re-run `install.ps1` (it grants the right with `secedit` unless `-SkipLogonRightGrant` is passed), or grant it by hand: `secpol.msc` > Local Policies > User Rights Assignment > **Log on as a service**. Then confirm the password with `sc.exe config <name> obj= <account> password= <password>`. |
| The service starts and immediately stops, exit code **3** | A startup refusal — see the table above. | Read `MiniVault cannot start: ...` in the Windows Application event log. |
| The MSI installs but leaves the service stopped | An MSI built before this fix used `ServiceControl` event `160`, which has no start-on-install bit. | Rebuild the MSI from the current `.aip` (event `163`), or start the service by hand once. |

### MSI validation (ICE) notes

Advanced Installer runs the standard ICE validation suite when it builds the package. Two things in
this project exist to keep it quiet, and should not be "simplified" away:

- Every component carries the 64-bit attribute (`256`), because the package is `MsiPackageType="x64"`.
  Without it **ICE80** fails the build and the registry rows would be redirected into `WOW6432Node`.
- `minivault.exe` is a static `MsiFilesComponent` row and is *excluded* from the synchronized folder,
  so exactly one component owns the file (**ICE30**: two components installing the same file to the
  same directory). The service rows need a named component whose `KeyPath` is that file, and the
  components a synchronized folder generates at build time have names nothing in the project can
  reference.

### Health and 503s

`GET /v1/health` never returns an error status — it always answers `200` with
`{"status":"ok","initialized":<bool>,"activeDataKeyVersion":<n>}`. `initialized: false` (with
`activeDataKeyVersion: 0`) means the vault has not been loaded yet — check the startup log for one of
the refusals above; a `curl -f`-based health check (as the Docker `HEALTHCHECK` uses) will still count
this as "healthy" since the HTTP status is 200, so also read the log for a real health signal.

Every *other* endpoint returns `503 vault_unavailable` when the master key or the database is
temporarily unreachable (a transient SQL error, a dropped connection, or a `MasterKeyUnavailableException`
raised mid-request) — this is not a startup refusal, it can happen at any time the database or key
provider hiccups, and normally self-resolves. Repeated `vault_unavailable` responses point at the
database (connectivity, load) rather than at MiniVault's own logic.

### Audit signals

- **`token.rejected` spikes** (client id `(anonymous)`) mean something is sending malformed, missing,
  or expired bearer tokens against protected endpoints — check the caller's IP and the recorded reason.
  Watch it alongside failed `token` rows (bad client credentials at `/v1/auth/token`); together they
  are what credential guessing and token replay look like from the audit trail.
- **`429` on `POST /v1/auth/token`** means more than `Token:LoginRateLimitPerMinute` (default 30)
  requests hit that endpoint in the current one-minute window, counted per server process. A
  legitimate client hitting this needs a lower retry rate or a higher limit; an unexpected burst is
  worth treating as a possible credential-guessing attempt, same as a `token.rejected`/failed-`token` spike.

## Pre-production checklist

**None of the items below has been executed.** The development machine that produced this repository
has no elevated shell, no Advanced Installer, no CI agent and no production-shaped SQL Server, so
every deployment path is written and unit-tested but never run end to end. Work through this list on
real hosts before the first production install, and record what you find.

### On an elevated Windows host (script path)

1. Clean install: `install.ps1 -CertificateThumbprint ...` with a real certificate in
   `LocalMachine\My`. The service must reach `Running`, and `https://localhost:8200/v1/health` must
   answer 200 **while running as LocalSystem**. Neither `MachineKeySet` certificate loading nor a
   DPAPI `LocalMachine` unwrap has ever run in a service context.
2. The same install with `-CertificatePath` / `-CertificatePassword`, using a password that contains
   a space.
3. `icacls "%ProgramData%\MiniVault"`: only SYSTEM, `BUILTIN\Administrators` and the service account;
   no `BUILTIN\Users`, no `Everyone`; inheritance disabled. Check `masterkey.bin` again after the
   first service start, since the server re-applies the ACL then.
4. A non-LocalSystem account: `-ServiceAccount CORP\svc -ServiceAccountPassword ...`. Record whether
   error 1069 appears and whether `SeServiceLogonRight` had to be granted separately.
5. Run `install.ps1` a **second** time on the same host, to confirm it upgrades in place.
6. `-SkipServiceStart`, then apply the SQL grant, then `sc.exe start`. Confirm the service works with
   only `db_datareader` + `db_datawriter`.
7. `minivault migrate` against an empty database and against an up-to-date one. Expect
   `Database is up to date.` on the second run, and an `AuditLog` row for each.
8. `uninstall.ps1` without `-PurgeData` leaves `%ProgramData%\MiniVault` behind. Only try
   `-PurgeData -Force` on a host you are about to discard.
9. **Full restore drill.** Back up the database, discard the host, then on a different machine run
   `install.ps1 -SkipInit -SkipServiceStart`, restore the database, run
   `minivault recover --new-master-key auto --share ...`, start the service, and read a secret that
   was written before the move. This is the single most important item on the list.

### On a machine with Advanced Installer (MSI path)

10. Build the MSI and read the ICE validation output, in particular for a duplicated `minivault.exe`
    component (ICE30) and the 64-bit component attributes (ICE80).
11. Silent install with `/l*v`. Search the log for the connection string, the PFX password and the
    master key: none of them may appear.
12. After the install the service is `Running`, `%ProgramData%\MiniVault\recovery-*.txt` is readable
    only by SYSTEM and Administrators, and the operator has copied and deleted it.
13. Major upgrade with no `MV_*` properties supplied: `appsettings.json` untouched, `init` skipped,
    service running again afterwards.
14. Uninstall leaves `%ProgramData%` in place, and a fresh install over it works.
15. Verify the `AdvancedInstaller@2` task's input names against the installed extension's
    `task.json` before setting `buildMsi=true`.

### On a CI agent

16. Nothing restores until `Karmasis.Cryptography` — preferably a stable version that carries a
    `netstandard2.0` target — is published to `artifactrepo` / `artifactrepodev`.
17. Confirm the `devops-vg` variable group holds `solution`, `BuildConfiguration`,
    `dockerRegistry*`, `project`, `groupId`, `vgMAJOR`/`vgMINOR`/`vgPATCH`/`vgRC` and
    `AdvancedInstallerPath`.
18. Run stages 1–2 first with `buildDocker=false` and `buildMsi=false`. Confirm `image_version`
    flows between stages and that the pack step picks it up.
19. Only then enable `buildDocker`, after the in-container feed credentials (the `TODO (DevOps team)`
    block) and the docker login are sorted out.
20. In the MSI stage, confirm the custom action tests actually run.

### In a container

21. The mounted PFX must be readable by uid 1654. Decide explicitly whether it is acceptable that
    `docker inspect` shows `MINIVAULT__MASTERKEY` in clear text to anyone with the Docker socket.
22. Confirm the container **refuses to start** when `Kestrel__Endpoints__Http__Url` is set, so no
    plain-HTTP port can be opened.
