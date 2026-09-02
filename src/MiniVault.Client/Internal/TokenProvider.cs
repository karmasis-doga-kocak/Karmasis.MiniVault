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

    private volatile CachedToken? _cached;

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
        var cached = _cached;
        if (cached is not null && cached.ExpiresAt > _now()) return cached.AccessToken;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check: another caller may have already refreshed while we were waiting for the gate.
            cached = _cached;
            if (cached is not null && cached.ExpiresAt > _now()) return cached.AccessToken;

            var now = _now();
            var response = await _http.RequestTokenAsync(_clientId, _clientSecret, ct).ConfigureAwait(false);

            var lifetime = response.ExpiresIn > RefreshMarginSeconds
                ? response.ExpiresIn - RefreshMarginSeconds
                : response.ExpiresIn;
            var expiresAt = now.AddSeconds(lifetime);

            _cached = new CachedToken(response.AccessToken, expiresAt);
            return response.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

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
