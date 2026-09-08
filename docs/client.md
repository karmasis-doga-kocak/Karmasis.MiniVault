# MiniVault Client

## 1. What it does

The MiniVault client fetches secrets from a MiniVault server over HTTPS, so a service never
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

`IMiniVaultClient` — the whole surface. It implements `IDisposable`; keep one per process.

| Member | Returns | Notes |
|---|---|---|
| `GetSecretAsync(name, ct)` | `Secret` | Falls back to the cache when the server is unreachable. |
| `SetSecretAsync(name, value, contentType, ct)` | `int` (the new version) | `value` is `byte[]`; `contentType` is optional. |
| `DeleteSecretAsync(name, ct)` | — | Also removes the name from the memory and disk caches. |
| `ListSecretsAsync(prefix, ct)` | `IReadOnlyList<SecretListItem>` | Names, versions and timestamps only — never values. Never cached. |
| `SecretServedFromCache` | `event` | Raised when a value came from the cache instead of the server. |

`Secret` carries `Name`, `Value` (`byte[]`), `ContentType`, `Version`, `UpdatedAt`, and `AsString()`
for UTF-8 text.

| Option | Default | Meaning | Validation |
|---|---|---|---|
| `BaseUrl` | *(required)* | Server URL, e.g. `https://minivault.local:8200`. | Non-empty, well-formed absolute URL, `https://` unless `AllowInsecureHttp` is set. |
| `ClientId` | *(required)* | The client id created with `minivault client add`. | Non-empty and `^[A-Za-z0-9._-]{1,128}$` — the server's own rule, which also keeps the id safe as the cache file's name. |
| `ClientSecret` | *(required)* | The secret printed once by `minivault client add`. | Non-empty. |
| `CacheDirectory` | `null` (disk cache off) | Directory for the encrypted on-disk cache. | — |
| `MaxCacheAge` | 7 days | How old a cached value can get before it is reported as stale. | Must be positive. |
| `RefreshInterval` | `null` (off) | How often cached secrets are refreshed in the background. | When set, at least 1 second. |
| `ServerCertificateThumbprint` | `null` | Pins the server's TLS certificate by SHA-1 thumbprint (for self-signed installs). | When set, must normalize to exactly 40 hex characters. |
| `Timeout` | 10 seconds | HTTP request timeout. | Between 1 second and 1 day. |
| `Log` | `null` | Optional `Action<string>` sink for diagnostic messages (cache fallbacks, background refresh failures). | — |
| `AllowInsecureHttp` | `false` | Allows `BaseUrl` to use `http://`. Development only. | — |

Options are validated by `MiniVaultClientFactory.Create` (and by `MiniVaultOptions.Validate()`
directly). Every rule in the last column throws `ArgumentException`, naming the offending option,
at startup rather than on first use.

## 3. Setup with the factory (.NET Framework / Ninject)

No dependency injection package is required. Build the client once, at startup, and keep it
for the lifetime of the process — creating a new client per request throws away the cache and
opens a new `HttpClient`.

```csharp
using Karmasis.MiniVault.Client;

var options = new MiniVaultOptions
{
    BaseUrl = "https://minivault.local:8200",
    ClientId = "dataskope-collector",
    ClientSecret = ReadClientSecret(), // see section 7
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

Keep **one** `IMiniVaultClient` per process, and dispose it at shutdown so the background refresh
timer and the underlying `HttpClient` are released cleanly. `Dispose()` waits for a refresh pass that
is running at that moment (for at most 10 seconds) and never writes the cache file after it has
returned, so tearing down the cache directory right after it is safe. Do not rely on the container for that
here: Ninject may not dispose an instance bound with `ToConstant()` — it did not create it — so
call `client.Dispose()` yourself in your shutdown path.

## 4. Setup with DI (`Microsoft.Extensions.DependencyInjection`)

```csharp
using Karmasis.MiniVault.Client;
using Karmasis.MiniVault.Client.DependencyInjection;

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
yourself. The registration is a `TryAdd`, so an `IMiniVaultClient` you registered yourself (a fake
in a test host, a decorator) stays in place and a second `AddMiniVaultClient` call does not build a
second client.

There is also an overload that binds the options from a configuration section:

```csharp
services.AddMiniVaultClient(configuration.GetSection("MiniVault"));
```

```json
{
  "MiniVault": {
    "BaseUrl": "https://minivault.local:8200",
    "ClientId": "dataskope-collector",
    "CacheDirectory": "C:\\ProgramData\\DataskopeCollector\\cache",
    "RefreshInterval": "00:05:00",
    "Timeout": "00:00:10"
  }
}
```

`TimeSpan` values use the standard text form (`00:05:00`, `7.00:00:00`). `Log` is a delegate and
cannot come from configuration; add the `Action<MiniVaultOptions>` overload as well if you want
one. **Do not put `ClientSecret` in the configuration file** — bind the rest from configuration and
set the secret from a protected store (section 7):

```csharp
services.AddMiniVaultClient(configuration.GetSection("MiniVault"));
services.Configure<MiniVaultOptions>(o => o.ClientSecret = ReadClientSecret(secretPath));
```

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

`ProtectedData` is not in the box everywhere: a **.NET Framework** project must reference
`System.Security.dll`, and a **.NET 6+ / netstandard** consumer must add the
`System.Security.Cryptography.ProtectedData` package. It is Windows-only in both cases.

```csharp
using System.IO;
using System.Security.Cryptography;
using System.Text;

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
as **stale** once it has not been confirmed by the server in that long.

An entry leaves the cache in exactly three ways: `DeleteSecretAsync` on the same name removes it;
a background refresh pass removes it when the server answers 404 (the secret was deleted) or 403
(the grant that allowed reading it was revoked); and any successful read replaces it with what the
server returned. `SetSecretAsync` does **not** remove it — the value you just wrote is what the
server now holds, so it is cached at the version the write returned. A process can therefore write
a secret, restart while the server is down, and still find that value on disk.

`SecretServedFromCache` fires whenever a value comes from the cache instead of a fresh server
answer, which happens when the server could not be reached (network error, timeout, 5xx, or 429) —
either on a `GetSecretAsync` of your own, or, with `RefreshInterval` set, on a background refresh
pass that failed to confirm an entry (see section 9):

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
value is kept and its "fetched at" timestamp is refreshed as if the server had sent it again —
in memory only, while the disk copy keeps the older timestamp, so a later offline start judges
staleness against the more pessimistic of the two and never reports a value fresher than it is.

## 9. Background refresh

Setting `RefreshInterval` changes the read path:

```csharp
o.RefreshInterval = TimeSpan.FromMinutes(5);
```

- A background timer re-reads every secret currently held in memory, on that interval, using
  a conditional GET (so an unchanged secret costs the server almost nothing). The minimum
  interval is 1 second.
- With `RefreshInterval` set, `GetSecretAsync` for a secret already in memory and younger than
  `MaxCacheAge` returns the in-memory value directly and does **not** make its own request — the
  timer is what keeps it current.
- An entry the timer has *not* managed to keep fresh — older than `MaxCacheAge` — is the exception:
  that read goes to the server after all. If the server answers, you get the fresh value and no
  event; only if the server is unreachable is the stale copy served, with `SecretServedFromCache`
  raised and `e.Stale` set.
- The `SecretServedFromCache` event can also fire **without** any call to `GetSecretAsync`: when a
  refresh pass cannot reach the server, it raises the event once for each entry it failed to
  confirm, with `e.Stale` saying whether that copy is by now older than `MaxCacheAge`.
- A refresh pass that gets 404 for a secret (deleted on the server) or 403 (the grant was revoked)
  drops that entry from memory and from disk, so the client stops serving a value it is no longer
  entitled to. Both cases are written to `Log`.
- The disk cache (if configured) is rewritten after a refresh pass only when something
  actually changed — a new value, or an eviction — not on every tick.

Background refresh only refreshes secrets you have already read at least once — it does not
discover or pull in new ones; listing is deliberately not used, since a client may be allowed
to read a secret without being allowed to list.

## 10. Errors

| Exception | HTTP status | Meaning | What to do |
|---|---|---|---|
| `MiniVaultAuthException` | 401 | Bad or expired credentials, or a bearer token that no longer works. | Check `ClientId`/`ClientSecret`. Confirm the client was not removed or disabled (`minivault client list`). The client already retries once on its own after a 401 by fetching a fresh token — this exception means that retry also failed. |
| `MiniVaultForbiddenException` | 403 | Authenticated, but no role grants access to that secret name (or the role is read-only and you called `SetSecretAsync`/`DeleteSecretAsync`). | Grant the scope: `minivault role grant <role> --scope <prefix> --permission <read\|write>`, then `minivault client assign`. |
| `MiniVaultNotFoundException` | 404 | No secret exists at that name. | Check the name for a typo, or write the secret first with `SetSecretAsync` (or from another client that holds write on that scope). |
| `MiniVaultRequestException` | 400 / 409 | 400: malformed input — a name outside 1–256 characters of letters, digits, `.`, `_`, `-` in `/`-separated segments (or with a segment made only of dots, such as `..`), a value over 1,048,576 bytes, or a `contentType` over 128 characters. 409: the secret was modified concurrently. | Fix the input for 400. For 409, retry the write once — it is an optimistic-concurrency conflict, not a bug. |
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
secret file from section 7:

```csharp
using System.Configuration;
using Karmasis.MiniVault.Client;
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
            ClientSecret = ReadClientSecret(secretPath), // section 7
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
using Karmasis.MiniVault.Client;

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
- Never let a human choose a client secret. Use the value `minivault client add` prints — it is
  generated with a cryptographic RNG. A memorable secret is a guessable one, and it is also the
  key the local cache file is encrypted with.
- `minivault client remove` and `minivault client disable` revoke access, but not instantly:
  a token the client already holds keeps working until it expires — 15 minutes by default.
  Plan for that window when responding to a suspected compromise.
- A revoked *grant* is picked up faster when `RefreshInterval` is set: the next refresh pass gets
  403 and drops the secret from the local caches. Without background refresh, the client keeps
  serving its cached copy until something asks for that secret again.
- Renewing the server's certificate means updating `ServerCertificateThumbprint` in **every**
  client that pins it. Plan the rollout before the renewal, not after (section 11).
- Never log a secret's value. Logging `Secret.Name`, `Version`, or `UpdatedAt` is fine and is
  what the `Log` option and the `SecretServedFromCache` event are for.
- The on-disk cache file is only as safe as the client secret: anyone who has the secret can
  decrypt the file, and anyone who has the file but not the secret gets nothing. It is a
  convenience for offline start, not a second credential to protect separately.
- The cache holds what the client has read *and what it has written*: `SetSecretAsync` leaves the
  written value in the local cache. Protect the cache directory accordingly.

## 14. Release prerequisites

These have to be settled before `Karmasis.MiniVault.Client` is published anywhere other people
consume it.

**Karmasis.Cryptography must ship `netstandard2.0` in a stable version.** The client encrypts its
disk cache with `Karmasis.Cryptography` (`AeadCipher`, `KeyDerivation.Hkdf`). Today only the
prerelease `26.3.0-dek.1`, from the local feed, carries a `netstandard2.0` target; the stable
`26.2.2` does not. Packing the client therefore produces `NU5104` (a stable package with a
prerelease dependency). **Do not publish `Karmasis.MiniVault.Client` to a shared feed until a
stable `Karmasis.Cryptography` with a `netstandard2.0` target exists**, and then bump the
`PackageReference` to it.

**Publish `Karmasis.MiniVault.Contracts` at the same version.** The client's public surface returns
contract types, so the two packages are versioned and released together; a consumer must never be
able to resolve a `Contracts` version the `Client` was not built against.

**Minimum .NET Framework is 4.7.1, documented as 4.7.2+.** Certificate pinning uses
`HttpClientHandler.ServerCertificateCustomValidationCallback`, which first appears in 4.7.1.
4.7.2 is the version to state as the requirement — it is what is actually deployed and supported.

**`packages.config` consumers need binding redirects.** A project that has not migrated to
`PackageReference` gets no automatic unification for the transitive dependencies of
`System.Text.Json`, and will fail at runtime with `FileLoadException` unless `app.config` (or
`web.config`) redirects them. Add, at minimum:

```xml
<configuration>
  <runtime>
    <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
      <dependentAssembly>
        <assemblyIdentity name="System.Runtime.CompilerServices.Unsafe" publicKeyToken="b03f5f7f11d50a3a" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-99.9.9.9" newVersion="6.0.0.0" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-99.9.9.9" newVersion="4.0.1.2" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Buffers" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-99.9.9.9" newVersion="4.0.3.0" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Threading.Tasks.Extensions" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-99.9.9.9" newVersion="4.2.0.1" />
      </dependentAssembly>
    </assemblyBinding>
  </runtime>
</configuration>
```

The `newVersion` values above are illustrative. Take the exact assembly version from the DLL that
NuGet actually restored (`packages\<Package>.<version>\lib\netstandard2.0\<Assembly>.dll` — check
its file properties, or run `[Reflection.AssemblyName]::GetAssemblyName($path).Version` in
PowerShell); a redirect to a version that is not on disk fails the same way as no redirect at all.

**Operational prerequisites**, worth repeating here because they are release-blocking in practice:
renewing the server's TLS certificate requires updating `ServerCertificateThumbprint` in every
client that pins it, and a client secret must always be the generated value from
`minivault client add` — never one a person chose.
