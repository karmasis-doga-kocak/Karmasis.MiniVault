using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Karmasis.MiniVault.Contracts;

namespace Karmasis.MiniVault.Client;

/// <summary>A client for the MiniVault secret store.</summary>
public interface IMiniVaultClient : IDisposable
{
    /// <summary>Retrieves a secret by name.</summary>
    Task<Secret> GetSecretAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Writes a secret's value, creating it if it does not exist. Returns the resulting version.
    /// </summary>
    Task<int> SetSecretAsync(string name, byte[] value, string? contentType = null, CancellationToken ct = default);

    /// <summary>Deletes a secret.</summary>
    Task DeleteSecretAsync(string name, CancellationToken ct = default);

    /// <summary>Lists secrets whose name starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<SecretListItem>> ListSecretsAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// Raised whenever the client falls back to its local copy of a secret because the server could not be
    /// reached (a network error, a timeout, an HTTP 5xx, or a 429). Exactly three situations fire it:
    /// <list type="bullet">
    /// <item><description>a <see cref="GetSecretAsync"/> whose request failed that way and was answered from the
    /// cache instead;</description></item>
    /// <item><description>a <see cref="GetSecretAsync"/> in background-refresh mode on an entry that has aged
    /// past <c>MaxCacheAge</c> — such a read is attempted live rather than served from memory, so it can fail
    /// the same way;</description></item>
    /// <item><description>a background refresh pass that could not reach the server, once per entry it failed
    /// to confirm — this one fires without any call of your own.</description></item>
    /// </list>
    /// It does <b>not</b> fire for a read answered from memory while background refresh is keeping that entry
    /// current, nor for an HTTP 304 (a confirmed live read, not a fallback), nor for 401/403/404/400/409, which
    /// are answers from a reachable server and are never served from the cache.
    /// <see cref="CacheServedEventArgs.Stale"/> says whether the copy that was served is older than
    /// <c>MaxCacheAge</c>. Handlers run on the thread that resolved the read (or on a timer thread for the
    /// background case); an exception thrown by a handler is logged and swallowed.
    /// </summary>
    event EventHandler<CacheServedEventArgs>? SecretServedFromCache;
}
