using System;
using System.Threading;
using System.Threading.Tasks;

namespace MiniVault.Client.Internal;

/// <summary>
/// Caches an access token obtained from <see cref="MiniVaultHttp.RequestTokenAsync"/> and refreshes it
/// automatically shortly before it expires. Concurrent callers observing an expired (or absent) token share a
/// single in-flight login via a semaphore; a failed login leaves no cached token behind.
/// </summary>
internal sealed class TokenProvider : IDisposable
{
    /// <summary>Refresh this many seconds before the token's actual expiry, unless the lifetime is too short for that margin.</summary>
    private const int RefreshMarginSeconds = 60;

    private readonly MiniVaultHttp _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    // Not `volatile`, so Interlocked.CompareExchange can be used on it without CS0420; every read goes through
    // Volatile.Read and every unconditional write through Volatile.Write instead.
    private CachedToken? _cached;

    public TokenProvider(MiniVaultHttp http, string clientId, string clientSecret, Func<DateTimeOffset> now)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        if (clientId is null) throw new ArgumentNullException(nameof(clientId));
        if (clientSecret is null) throw new ArgumentNullException(nameof(clientSecret));
        if (now is null) throw new ArgumentNullException(nameof(now));

        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _now = now;
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && cached.ExpiresAt > _now()) return cached.AccessToken;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check: another caller may have already refreshed while we were waiting for the gate.
            cached = Volatile.Read(ref _cached);
            if (cached is not null && cached.ExpiresAt > _now()) return cached.AccessToken;

            var now = _now();
            var response = await _http.RequestTokenAsync(_clientId, _clientSecret, ct).ConfigureAwait(false);

            // A token whose whole lifetime is shorter than the refresh margin would be cached for its full
            // stated lifetime and then used at the very last moment; half of it (at least one second) leaves
            // room for the request it is attached to to actually reach the server.
            var lifetime = response.ExpiresIn > RefreshMarginSeconds
                ? response.ExpiresIn - RefreshMarginSeconds
                : Math.Max(1, response.ExpiresIn / 2);
            var expiresAt = now.AddSeconds(lifetime);

            Volatile.Write(ref _cached, new CachedToken(response.AccessToken, expiresAt));
            return response.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached token unconditionally, so the next <see cref="GetAsync"/> logs in again. Production code
    /// uses <see cref="Invalidate(string)"/> instead; this overload exists for tests and for callers that want an
    /// unconditional reset.
    /// </summary>
    public void Invalidate() => Volatile.Write(ref _cached, null);

    /// <summary>
    /// Drops the cached token only if it is still the one the caller just used, compared ordinally. Two callers
    /// that both fail with 401 while holding the same token therefore cause exactly one re-login: the first
    /// clears the cache, the second finds the already-refreshed token and leaves it alone. A caller whose stale
    /// token has meanwhile been replaced never throws away the newer token.
    /// </summary>
    /// <param name="staleToken">The access token that was just rejected.</param>
    public void Invalidate(string staleToken)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is null) return;
        if (!string.Equals(cached.AccessToken, staleToken, StringComparison.Ordinal)) return;

        // Only clears if no one else has replaced the entry since it was read.
        Interlocked.CompareExchange(ref _cached, null, cached);
    }

    /// <summary>Releases the semaphore that serializes concurrent logins.</summary>
    public void Dispose() => _gate.Dispose();

    private sealed class CachedToken
    {
        public CachedToken(string accessToken, DateTimeOffset expiresAt)
        {
            AccessToken = accessToken;
            ExpiresAt = expiresAt;
        }

        public string AccessToken { get; }
        public DateTimeOffset ExpiresAt { get; }
    }
}
