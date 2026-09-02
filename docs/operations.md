# MiniVault operations

This page covers the operator commands. Installation (Windows setup, Docker) is documented in a later release.

## How the keys fit together

- **Master key (KEK)** — 32 random bytes. Lives only on the MiniVault host: a DPAPI-protected file on Windows, the `MINIVAULT__MASTERKEY` environment variable in a container. It never goes into the database.
- **Data keys (DEK)** — encrypt the secret values. Each DEK is stored in the database twice: wrapped by the master key, and wrapped by the recovery key.
- **Recovery key** — shown once by `init`. Lets you replace a lost or forgotten master key. Keep it offline; in Shamir mode split it between people.

Losing both the master key and the recovery material means the secrets are gone. There is no back door.

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
| `--master-key <password>` | Derive the master key from a password (PBKDF2, salt and iteration count are stored in the database). Without it a random key is generated. The password is used only to derive the key at this moment; it is **not** a way back in later — if the master key file or environment value is lost, only the recovery material helps. Passing it on the command line exposes it to shell history and process listings; prefer omitting it (random key) or run the command from the installer. |
| `--out <file>` | Also write the output to a file. Delete the file after the material is stored safely. The file is created with permissions for the current user only and is never overwritten; delete it after the material is stored safely. |
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

With the `Environment` provider the last line instead prints the master key; set it as `MINIVAULT__MASTERKEY` before starting the server.

With the `Environment` provider the master key is printed to standard output — in Docker that means `docker logs`; clear or rotate the log after copying the value.

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

Restart the MiniVault service after rotating; the running server loads data keys at startup and will not see the new version until it restarts.

### `minivault` (no command) / `minivault serve`

Starts the server. It refuses to start when the vault is not initialized or the master key does not unwrap the data keys; the reason is written to the log.

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

Every command above writes an audit row with client id `cli`. The action names are `client.add`, `client.remove`, `client.assign`, `client.enable`, `client.disable`, `role.add`, `role.remove`, `role.grant`.

## Master key providers

| `MasterKey:Provider` | Where the key lives | Notes |
|---|---|---|
| `Dpapi` (default) | `%ProgramData%\MiniVault\masterkey.bin`, DPAPI LocalMachine | Windows only. Bound to the machine: the file cannot be read on another host. `MasterKey:Path` overrides the location. |
| `Environment` | `MINIVAULT__MASTERKEY` (base64, 32 bytes) | Containers / Linux. `init` prints the value once. |

## Backups

Back up two things separately: the database (normal SQL Server backup) and the recovery material. Neither is useful alone.

## HTTP API

TLS/HTTPS configuration ships with the installer and container images (a later release); today the server listens on plain HTTP and is expected to sit behind a host that terminates TLS, or to be used only on a trusted local network, until that lands.

| Method | Path | Auth | Success | Error codes |
|---|---|---|---|---|
| `POST` | `/v1/auth/token` | none | 200 | `invalid_request`, `unauthorized` |
| `GET` | `/v1/secrets/{name}` | Bearer | 200 (304 if `If-None-Match` matches) | `invalid_request`, `unauthorized`, `forbidden`, `not_found` |
| `PUT` | `/v1/secrets/{name}` | Bearer | 200 | `invalid_request`, `unauthorized`, `forbidden`, `conflict` |
| `DELETE` | `/v1/secrets/{name}` | Bearer | 204 | `invalid_request`, `unauthorized`, `forbidden`, `not_found` |
| `GET` | `/v1/secrets?prefix=` | Bearer | 200 | `unauthorized`, `forbidden` |
| `GET` | `/v1/health` | none | 200 | — |

Any endpoint can also return `vault_unavailable` (503, the master key or database is temporarily unreachable) or `internal_error` (500, unexpected failure); both are logged server-side.

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
| `invalid_request` | Malformed input: bad secret name, missing/non-base64 `value`, missing token fields, oversized value or content type. |
| `conflict` | The secret was modified concurrently (optimistic concurrency); retry the request. |
| `vault_unavailable` | The vault is temporarily unavailable (master key or database unreachable). |
| `internal_error` | Unexpected server failure. |

### Example: token, write, read with ETag, conditional read

```
curl -s -X POST http://localhost:5000/v1/auth/token \
  -H "Content-Type: application/json" \
  -d '{"clientId":"c","clientSecret":"<client secret>"}'
# {"accessToken":"eyJ...","expiresIn":900}

TOKEN=eyJ...

curl -s -X PUT http://localhost:5000/v1/secrets/test/one \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"value":"aGVsbG8=","contentType":"text/plain"}'
# {"version":1}

curl -si http://localhost:5000/v1/secrets/test/one -H "Authorization: Bearer $TOKEN"
# HTTP/1.1 200 OK
# ETag: "1"
# {"name":"test/one","value":"aGVsbG8=","contentType":"text/plain","version":1,"updatedAt":"..."}

curl -si http://localhost:5000/v1/secrets/test/one \
  -H "Authorization: Bearer $TOKEN" -H 'If-None-Match: "1"'
# HTTP/1.1 304 Not Modified
```
