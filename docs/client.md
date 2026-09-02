# MiniVault Client

## 1. What it does

The MiniVault client fetches secrets from a MiniVault server over HTTP, so a service never
has to embed its own copy of a connection string or an API key. It authenticates with a
client id and secret, caches what it reads (in memory, and optionally on disk) so a brief
server outage does not stop the service from starting, and raises typed exceptions instead
of exposing HTTP status codes to your code.

### Install

```
dotnet add package Karmasis.MiniVault.Client
dotnet add package Karmasis.MiniVault.Client.DependencyInjection   # optional, for ASP.NET Core / generic host
```

Both packages target `netstandard2.0`, so they run on .NET Framework 4.7.2+ and on .NET 8+
(and anything newer). The `.DependencyInjection` package is only needed if you use
`Microsoft.Extensions.DependencyInjection`; a classic .NET Framework app (Ninject, or no
container at all) only needs `Karmasis.MiniVault.Client`.

## 2. Quick reference

| Option | Default | Meaning |
|---|---|---|
| `BaseUrl` | *(required)* | Server URL, e.g. `https://minivault.local:8200`. Must be `https://` unless `AllowInsecureHttp` is set. |
| `ClientId` | *(required)* | The client id created with `minivault client add`. |
| `ClientSecret` | *(required)* | The secret printed once by `minivault client add`. |
| `CacheDirectory` | `null` (disk cache off) | Directory for the encrypted on-disk cache. |
| `MaxCacheAge` | 7 days | How old a cached value can get before it is reported as stale. |
| `RefreshInterval` | `null` (off) | How often cached secrets are refreshed in the background. |
| `ServerCertificateThumbprint` | `null` | Pins the server's TLS certificate by SHA-1 thumbprint (for self-signed installs). |
| `Timeout` | 10 seconds | HTTP request timeout. |
| `Log` | `null` | Optional `Action<string>` sink for diagnostic messages (cache fallbacks, background refresh failures). |
| `AllowInsecureHttp` | `false` | Allows `BaseUrl` to use `http://`. Development only. |

Options are validated by `MiniVaultClientFactory.Create` (and by `MiniVaultOptions.Validate()`
directly): a missing required field, a malformed `BaseUrl`, an `http://` `BaseUrl` without
`AllowInsecureHttp`, a non-positive `Timeout`, or a `ServerCertificateThumbprint` that does not
normalize to 40 hex characters all throw `ArgumentException` at startup, not on first use.

## 3. Setup with the factory (.NET Framework / Ninject)

No dependency injection package is required. Build the client once, at startup, and keep it
for the lifetime of the process — creating a new client per request throws away the cache and
opens a new `HttpClient`.

```csharp
using MiniVault.Client;

var options = new MiniVaultOptions
{
    BaseUrl = "https://minivault.local:8200",
    ClientId = "dataskope-collector",
    ClientSecret = ReadClientSecret(), // see section 6
    CacheDirectory = @"C:\ProgramData\DataskopeCollector\cache",
};

IMiniVaultClient client = MiniVaultClientFactory.Create(options);
```

With Ninject, bind the already-constructed instance so the container hands out the same
client everywhere:

```csharp
public class MiniVaultModule : NinjectModule
{
    public override void Load()
    {
        var options = new MiniVaultOptions
        {
            BaseUrl = "https://minivault.local:8200",
            ClientId = "dataskope-collector",
            ClientSecret = ReadClientSecret(),
            CacheDirectory = @"C:\ProgramData\DataskopeCollector\cache",
        };

        var client = MiniVaultClientFactory.Create(options);

        Bind<IMiniVaultClient>().ToConstant(client);
    }
}
```

Keep **one** `IMiniVaultClient` per process. Dispose it at shutdown (Ninject disposes a
`ToConstant()`-bound `IDisposable` when the kernel is disposed; otherwise call
`client.Dispose()` yourself in your shutdown path) so the background refresh timer and the
underlying `HttpClient` are released cleanly.

## 4. Setup with DI (`Microsoft.Extensions.DependencyInjection`)

```csharp
using MiniVault.Client;
using MiniVault.Client.DependencyInjection;

services.AddMiniVaultClient(o =>
{
    o.BaseUrl = "https://minivault.local:8200";
    o.ClientId = "dataskope-collector";
    o.ClientSecret = configuration["MiniVault:ClientSecret"];
    o.CacheDirectory = @"C:\ProgramData\DataskopeCollector\cache";
});
```

This registers `IMiniVaultClient` as a singleton, created lazily on first resolution. It is
disposed automatically when the container is disposed — you do not need to dispose it
yourself.

## 5. Reading a text secret

```csharp
Secret secret = await client.GetSecretAsync("dataskope/collector/connection-string");
string connectionString = secret.AsString();
```

`Secret.AsString()` decodes `Secret.Value` as UTF-8. Use it for connection strings, API keys,
and any other secret whose value is text.

## 6. Reading a PFX

Store the certificate's password as its own secret, next to the certificate, and build the
`X509Certificate2` from the two:

```csharp
using System.Security.Cryptography.X509Certificates;

Secret pfx = await client.GetSecretAsync("dataskope/collector/cert");
Secret pfxPassword = await client.GetSecretAsync("dataskope/collector/cert-password");

var certificate = new X509Certificate2(
    pfx.Value,
    pfxPassword.AsString(),
    X509KeyStorageFlags.MachineKeySet);
```

`X509KeyStorageFlags.MachineKeySet` keeps the imported private key out of the per-user
profile, which matters for a service running under a machine account.

## 7. Storing the client secret on Windows with DPAPI

Never put the client secret in plain text in `app.config`, `appsettings.json`, or source
control. Protect it once with DPAPI (`LocalMachine` scope, so any process running as a local
service account on that machine can unprotect it) and store only the encrypted bytes:

```csharp
using System.IO;
using System.Security.Cryptography;

// One-time setup step, e.g. run interactively during installation.
static void ProtectClientSecret(string plainTextSecret, string filePath)
{
    byte[] plainBytes = Encoding.UTF8.GetBytes(plainTextSecret);
    byte[] protectedBytes = ProtectedData.Protect(
        plainBytes,
        optionalEntropy: null,
        scope: DataProtectionScope.LocalMachine);

    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    File.WriteAllBytes(filePath, protectedBytes);
}

// At service startup.
static string ReadClientSecret(string filePath)
{
    byte[] protectedBytes = File.ReadAllBytes(filePath);
    byte[] plainBytes = ProtectedData.Unprotect(
        protectedBytes,
        optionalEntropy: null,
        scope: DataProtectionScope.LocalMachine);

    return Encoding.UTF8.GetString(plainBytes);
}
```

Store the file under `%ProgramData%\<Product>\`, e.g.
`C:\ProgramData\DataskopeCollector\clientsecret.bin`. Restrict its ACLs the same way you would
restrict the MiniVault server's own master key file (`docs/operations.md`): only the service
account and administrators should be able to read it. DPAPI's `LocalMachine` scope stops
casual disk access from other user profiles, but it does not stop another process running as
the same machine identity — the ACL is what does that.

## 8. Cache and offline start

The client keeps every secret it has successfully read in memory for the life of the process.
When `CacheDirectory` is set, that same set of secrets is also written to an encrypted file on
disk:

```
{CacheDirectory}/{ClientId}.cache
```

The file is encrypted with a key derived (HKDF) from the client secret, so reading it back
requires the same `ClientId` and `ClientSecret` that wrote it — the file alone, without the
secret, is not enough to recover the vault contents. This is also why an offline start still
works after a service restart: the client loads the disk cache into memory as soon as it is
constructed, before it ever talks to the server.

`MaxCacheAge` (default 7 days) does not expire cache entries outright. It marks a cached value
as **stale** once it has not been confirmed by the server in that long. A value only leaves
the cache path in one of two ways: the server answers with a value, or you call
`SetSecretAsync`/`DeleteSecretAsync` on the same name.

`SecretServedFromCache` fires whenever a value comes from the cache instead of a fresh server
answer — which only happens when the server could not be reached (network error, timeout,
5xx, or 429):

```csharp
client.SecretServedFromCache += (sender, e) =>
{
    logger.LogWarning(
        "MiniVault: served '{Name}' from cache (fetched {FetchedAt:O}, stale: {Stale})",
        e.Name, e.FetchedAt, e.Stale);
};
```

`e.Stale` is `true` when the cached copy is older than `MaxCacheAge` — use it to decide
whether to page someone, versus just logging and continuing.

A normal, reachable server exchange that returns HTTP 304 (the client's cached version is
still current) is **not** a cache-served event: it is a confirmed live read, so the cached
value is kept and its "fetched at" timestamp is refreshed as if the server had sent it again.

## 9. Background refresh

Setting `RefreshInterval` changes the read path:

```csharp
o.RefreshInterval = TimeSpan.FromMinutes(5);
```

- A background timer re-reads every secret currently held in memory, on that interval, using
  a conditional GET (so an unchanged secret costs the server almost nothing).
- With `RefreshInterval` set, `GetSecretAsync` for a secret already in memory returns the
  in-memory value directly and does **not** make its own request — the timer is what keeps it
  current.
- The `SecretServedFromCache` event can still fire **without** any call to `GetSecretAsync`:
  if the background refresh cannot reach the server, the in-memory copy it is still serving
  is reported as stale (once past `MaxCacheAge`) through the same event.
- The disk cache (if configured) is rewritten after a refresh pass only when something
  actually changed, not on every tick.

Background refresh only refreshes secrets you have already read at least once — it does not
discover or pull in new ones; listing is deliberately not used, since a client may be allowed
to read a secret without being allowed to list.

## 10. Errors

| Exception | HTTP status | Meaning | What to do |
|---|---|---|---|
| `MiniVaultAuthException` | 401 | Bad or expired credentials, or a bearer token that no longer works. | Check `ClientId`/`ClientSecret`. Confirm the client was not removed or disabled (`minivault client list`). The client already retries once on its own after a 401 by fetching a fresh token — this exception means that retry also failed. |
| `MiniVaultForbiddenException` | 403 | Authenticated, but no role grants access to that secret name (or the role is read-only and you called `SetSecretAsync`/`DeleteSecretAsync`). | Grant the scope: `minivault role grant <role> --scope <prefix> --permission <read\|write>`, then `minivault client assign`. |
| `MiniVaultNotFoundException` | 404 | No secret exists at that name. | Check the name. Create it with `minivault client add` + `SetSecretAsync`, or fix a typo. |
| `MiniVaultRequestException` | 400 / 409 | 400: malformed input (bad name, non-base64 value, oversized value). 409: the secret was modified concurrently. | Fix the input for 400. For 409, retry the write once — it is an optimistic-concurrency conflict, not a bug. |
| `MiniVaultUnavailableException` | network error, timeout, 5xx, 429 | The server could not be reached, took too long, or is temporarily overloaded/rate-limited. | Retry with backoff. `GetSecretAsync` already falls back to the cache automatically when one is available — you only see this exception when there is nothing cached to fall back to. |

Branch on the exception **type** (or on `MiniVaultException.ErrorCode`, which mirrors the
server's `error` field from `docs/operations.md`), never on `Exception.Message` or on
`ErrorResponse.Detail`. `Detail` is a free-text diagnostic string meant for logs, not for
program logic, and its wording is not part of the contract.

## 11. TLS

Production servers should present a certificate from a trusted CA, in which case the client
needs no special configuration — the platform's normal certificate chain validation applies.

For a self-signed or internal-CA install where that chain cannot be validated normally, pin
the server's certificate by thumbprint instead of disabling validation:

```csharp
o.ServerCertificateThumbprint = "AB12CD34EF56...."; // 40 hex characters (SHA-1)
```

Get the thumbprint from the server host:

```powershell
Get-ChildItem Cert:\LocalMachine\My | Select-Object Thumbprint, Subject
```

or from the certificate's Details tab in the Windows certificate MMC (`certlm.msc`). Both
sources are fine as input: separators (`:`/`-`), spaces, and the invisible left-to-right-mark
character the MMC sometimes prepends when you copy a thumbprint are all stripped before
comparison. A value that does not normalize to exactly 40 hex characters throws
`ArgumentException` when the client is created — a broken pin fails at startup, not on the
first request.

Pinning **replaces** chain validation; it does not add to it. A pinned certificate that has
expired, or that a normal chain check would reject for any other reason, is still accepted as
long as its thumbprint matches. This means that when the server's certificate is renewed, the
pin must be updated too, or every client will start rejecting the new (valid) certificate.

Certificate validation is never disabled by this client. `AllowInsecureHttp` only lets
`BaseUrl` use `http://` instead of `https://` for local development — it does not weaken TLS
validation when `https://` is used, and it should never be set in production.

## 12. Classic Collector example

A Ninject module that builds the client from `app.config` settings plus the DPAPI-protected
secret file from section 6:

```csharp
using System.Configuration;
using MiniVault.Client;
using Ninject.Modules;

public class MiniVaultModule : NinjectModule
{
    public override void Load()
    {
        var secretPath = ConfigurationManager.AppSettings["MiniVault:ClientSecretFile"];

        var options = new MiniVaultOptions
        {
            BaseUrl = ConfigurationManager.AppSettings["MiniVault:BaseUrl"],
            ClientId = ConfigurationManager.AppSettings["MiniVault:ClientId"],
            ClientSecret = ReadClientSecret(secretPath), // section 6
            CacheDirectory = ConfigurationManager.AppSettings["MiniVault:CacheDirectory"],
            RefreshInterval = TimeSpan.FromMinutes(5),
            ServerCertificateThumbprint = ConfigurationManager.AppSettings["MiniVault:ServerCertificateThumbprint"],
        };

        Bind<IMiniVaultClient>().ToConstant(MiniVaultClientFactory.Create(options));
    }
}
```

A startup class that resolves the certificate the Collector uses to talk to Dataskope, and
logs whenever it is served from cache:

```csharp
using System.Security.Cryptography.X509Certificates;
using MiniVault.Client;

public class DataskopeCertificateProvider
{
    private readonly IMiniVaultClient _vault;
    private readonly ILogger _logger;

    public DataskopeCertificateProvider(IMiniVaultClient vault, ILogger logger)
    {
        _vault = vault;
        _logger = logger;

        _vault.SecretServedFromCache += (sender, e) =>
            _logger.Warn($"MiniVault served '{e.Name}' from cache (stale: {e.Stale}, fetched {e.FetchedAt:O}).");
    }

    public async Task<X509Certificate2> GetCertificateAsync()
    {
        var pfx = await _vault.GetSecretAsync("dataskope/collector/cert");
        var password = await _vault.GetSecretAsync("dataskope/collector/cert-password");

        return new X509Certificate2(
            pfx.Value,
            password.AsString(),
            X509KeyStorageFlags.MachineKeySet);
    }
}
```

## 13. Security notes

- The client secret **is** the identity. Anyone who has it can do everything the client's
  roles allow, from any machine that can reach the server. Treat it like a password, not like
  a connection string that is merely inconvenient to leak.
- Roles limit blast radius. A client scoped to `dataskope/collector/` cannot read or write
  anything outside that prefix even if its secret leaks — grant the narrowest scope that
  works (`docs/operations.md`, `minivault role grant`).
- `minivault client remove` and `minivault client disable` revoke access, but not instantly:
  a token the client already holds keeps working until it expires — 15 minutes by default.
  Plan for that window when responding to a suspected compromise.
- Never log a secret's value. Logging `Secret.Name`, `Version`, or `UpdatedAt` is fine and is
  what the `Log` option and the `SecretServedFromCache` event are for.
- The on-disk cache file is only as safe as the client secret: anyone who has the secret can
  decrypt the file, and anyone who has the file but not the secret gets nothing. It is a
  convenience for offline start, not a second credential to protect separately.
