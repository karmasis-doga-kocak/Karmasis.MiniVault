# MiniVault operations

This page covers the operator commands. Installation (Windows setup, Docker) is documented in a later release.

## How the keys fit together

- **Master key (KEK)** — 32 random bytes. Lives only on the MiniVault host: a DPAPI-protected file on Windows, the `MINIVAULT__MASTERKEY` environment variable in a container. It never goes into the database.
- **Data keys (DEK)** — encrypt the secret values. Each DEK is stored in the database twice: wrapped by the master key, and wrapped by the recovery key.
- **Recovery key** — shown once by `init`. Lets you replace a lost or forgotten master key. Keep it offline; in Shamir mode split it between people.

Losing both the master key and the recovery material means the secrets are gone. There is no back door.

## Commands

All commands read the same configuration as the server: `appsettings.json` next to the binary, then `%ProgramData%\MiniVault\appsettings.json` (Windows), then environment variables, then command-line overrides such as `--ConnectionStrings:MiniVault "..."`.

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
| `--master-key <password>` | Derive the master key from a password (PBKDF2, salt and iteration count are stored in the database). Without it a random key is generated. |
| `--out <file>` | Also write the output to a file. Delete the file after the material is stored safely. |

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

With the `Environment` provider the last line instead prints the master key; set it as `MINIVAULT__MASTERKEY` before starting the server.

### `minivault recover`

Replaces the master key using the recovery material. Every data key is rewrapped under the new master key; secrets are not touched. Use it when the master key is lost, or simply to change it.

```
minivault recover --new-master-key auto --recovery-key <key>
minivault recover --new-master-key "new passphrase" --share <share1> --share <share3>
```

`auto` generates a random master key. Any `threshold` shares work, in any order.

### `minivault rotate-dek`

Creates a new active data key. New and updated secrets use it; existing secrets stay readable with their old key. Needs the master key (it unwraps the stored recovery key to wrap the new data key).

```
minivault rotate-dek
```

### `minivault` (no command) / `minivault serve`

Starts the server. It refuses to start when the vault is not initialized or the master key does not unwrap the data keys; the reason is written to the log.

## Master key providers

| `MasterKey:Provider` | Where the key lives | Notes |
|---|---|---|
| `Dpapi` (default) | `%ProgramData%\MiniVault\masterkey.bin`, DPAPI LocalMachine | Windows only. Bound to the machine: the file cannot be read on another host. `MasterKey:Path` overrides the location. |
| `Environment` | `MINIVAULT__MASTERKEY` (base64, 32 bytes) | Containers / Linux. `init` prints the value once. |

## Backups

Back up two things separately: the database (normal SQL Server backup) and the recovery material. Neither is useful alone.
