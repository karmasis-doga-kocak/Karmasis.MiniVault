using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MiniVault.Client.Internal;
using MiniVault.Contracts;

namespace MiniVault.Client;

/// <summary>
/// The MiniVault client: token handling, an in-memory cache backed by an optional encrypted disk cache, a
/// conditional-GET read path, and an optional background refresh. Instances are created through
/// <see cref="MiniVaultClientFactory"/>.
/// </summary>
internal sealed class MiniVaultClient : IMiniVaultClient
{
    private readonly MiniVaultOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly HttpClient _httpClient;
    private readonly MiniVaultHttp _http;
    private readonly TokenProvider _tokens;
    private readonly MemoryCache _memory = new MemoryCache();
    private readonly DiskCache? _disk;
    private readonly Timer? _timer;

    /// <summary>Guards against overlapping background refresh ticks (0 = idle, 1 = running).</summary>
    private int _refreshing;

    private int _disposed;

    /// <summary>
    /// Creates a client over the given message handler. The handler's lifetime is taken over by this instance:
    /// <see cref="Dispose"/> disposes it along with the underlying <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="options">Validated on construction.</param>
    /// <param name="handler">The message handler to send requests through.</param>
    /// <param name="now">The clock, injectable so tests can control cache ages and token expiry.</param>
    public MiniVaultClient(MiniVaultOptions options, HttpMessageHandler handler, Func<DateTimeOffset> now)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        if (now is null) throw new ArgumentNullException(nameof(now));

        options.Validate();

        _options = options;
        _now = now;

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl)),
            Timeout = options.Timeout,
        };
        _http = new MiniVaultHttp(_httpClient);
        _tokens = new TokenProvider(_http, options.ClientId, options.ClientSecret, now);

        if (!string.IsNullOrWhiteSpace(options.CacheDirectory))
        {
            _disk = new DiskCache(options.CacheDirectory!, options.ClientId, options.ClientSecret, options.Log);
            // Loaded once, at construction: an offline start finds the previous run's secrets in memory.
            foreach (var entry in _disk.Load()) _memory.Set(entry);
        }

        if (options.RefreshInterval.HasValue)
        {
            var interval = options.RefreshInterval.Value;
            _timer = new Timer(OnTimerTick, null, interval, interval);
        }
    }

    /// <inheritdoc />
    public event EventHandler<CacheServedEventArgs>? SecretServedFromCache;

    /// <summary>The base address the underlying <see cref="HttpClient"/> resolves relative paths against.</summary>
    internal Uri? BaseAddress => _httpClient.BaseAddress;

    /// <inheritdoc />
    public async Task<Secret> GetSecretAsync(string name, CancellationToken ct = default)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        ThrowIfDisposed();

        var cached = TryGetCached(name);

        // With background refresh on, memory is kept current by the timer; a read never goes to the server.
        if (_options.RefreshInterval.HasValue && cached is not null)
        {
            var stale = _now() - cached.FetchedAt > _options.MaxCacheAge;
            if (stale) RaiseServedFromCache(name, stale: true, cached.FetchedAt);
            return cached.ToSecret();
        }

        try
        {
            var result = await WithTokenAsync(token => _http.GetSecretAsync(name, token, cached?.Version, ct), ct).ConfigureAwait(false);

            if (result.NotModified)
            {
                if (cached is null)
                    throw new MiniVaultRequestException(
                        $"The MiniVault server answered 304 Not Modified for '{name}', which is not cached locally.",
                        result.Status);

                // The cached copy was just confirmed current, so it counts as freshly fetched. Memory only:
                // rewriting the disk cache on every conditional hit would be a lot of I/O for no new value.
                var confirmed = Confirm(cached);
                _memory.Set(confirmed);
                return confirmed.ToSecret();
            }

            var entry = ToEntry(name, result.Body);
            _memory.Set(entry);
            PersistDisk();
            return entry.ToSecret();
        }
        catch (MiniVaultUnavailableException)
        {
            // Network error, timeout, 429 or 5xx: fall back to the cache, but only for this failure kind.
            // 401/403/404/400/409 are answers from a reachable server and are never served from cache.
            var fallback = cached ?? TryGetCached(name);
            if (fallback is null) throw;

            var stale = _now() - fallback.FetchedAt > _options.MaxCacheAge;
            _options.Log?.Invoke($"MiniVault served '{name}' from the local cache (fetched {fallback.FetchedAt:O}, stale: {stale}).");
            // Raised outside any lock, and after all cache state has settled.
            RaiseServedFromCache(name, stale, fallback.FetchedAt);
            return fallback.ToSecret();
        }
    }

    /// <inheritdoc />
    public async Task<int> SetSecretAsync(string name, byte[] value, string? contentType = null, CancellationToken ct = default)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));
        ThrowIfDisposed();

        var body = new SetSecretRequest { Value = Convert.ToBase64String(value), ContentType = contentType };
        var response = await WithTokenAsync(token => _http.PutSecretAsync(name, token, body, ct), ct).ConfigureAwait(false);

        // The cached copy is now a previous version; drop it rather than guessing at the new one.
        Invalidate(name);
        return response.Version;
    }

    /// <inheritdoc />
    public async Task DeleteSecretAsync(string name, CancellationToken ct = default)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        ThrowIfDisposed();

        await WithTokenAsync(async token =>
        {
            await _http.DeleteSecretAsync(name, token, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

        Invalidate(name);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretListItem>> ListSecretsAsync(string prefix, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return await WithTokenAsync(token => _http.ListAsync(prefix, token, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the background refresh and releases the underlying <see cref="HttpClient"/> (and its handler) and
    /// the token provider. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _timer?.Dispose();
        _httpClient.Dispose();
        _tokens.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="action"/> with a valid access token. A 401 invalidates the cached token and the
    /// action is retried exactly once with a freshly issued one; a second 401 propagates. Tokens are not
    /// revoked when a client's grants change, so a 401 is the only signal that a retry is worth attempting.
    /// </summary>
    private async Task<T> WithTokenAsync<T>(Func<string, Task<T>> action, CancellationToken ct)
    {
        var token = await _tokens.GetAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(token).ConfigureAwait(false);
        }
        catch (MiniVaultAuthException)
        {
            _tokens.Invalidate();
            var refreshed = await _tokens.GetAsync(ct).ConfigureAwait(false);
            return await action(refreshed).ConfigureAwait(false);
        }
    }

    private void OnTimerTick(object? state)
    {
        // A tick that arrives while the previous one is still running is skipped rather than queued.
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _ = RunRefreshAsync();
    }

    private async Task RunRefreshAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"MiniVault background refresh failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// Re-reads every secret currently held in memory with a conditional GET. Listing is deliberately not used:
    /// a client may be allowed to read its own secrets without being allowed to list. Per-secret failures are
    /// logged and skipped, so one unreadable secret does not stop the rest from refreshing. The disk cache is
    /// written at most once, after the loop, and only if some secret actually changed — a conditional-GET-only
    /// confirmation never touches disk, and a run of many secrets does not do one disk write per secret.
    /// </summary>
    private async Task RefreshAsync(CancellationToken ct)
    {
        var changed = false;

        foreach (var entry in _memory.Snapshot())
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            try
            {
                var result = await WithTokenAsync(token => _http.GetSecretAsync(entry.Name, token, entry.Version, ct), ct).ConfigureAwait(false);

                if (result.NotModified)
                {
                    _memory.Set(Confirm(entry));
                    continue;
                }

                _memory.Set(ToEntry(entry.Name, result.Body));
                changed = true;
            }
            catch (Exception ex)
            {
                // Once disposed, the client is shutting down: a failure here is expected noise, not diagnostic.
                if (Volatile.Read(ref _disposed) == 0)
                    _options.Log?.Invoke($"MiniVault background refresh of '{entry.Name}' failed: {ex.Message}");
            }
        }

        if (changed) PersistDisk();
    }

    /// <summary>
    /// Raises <see cref="SecretServedFromCache"/>, swallowing any exception a subscriber's handler throws so it
    /// can never replace an already-resolved result with a fault.
    /// </summary>
    private void RaiseServedFromCache(string name, bool stale, DateTimeOffset fetchedAt)
    {
        try
        {
            SecretServedFromCache?.Invoke(this, new CacheServedEventArgs(name, stale, fetchedAt));
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"MiniVault SecretServedFromCache handler threw: {ex.Message}");
        }
    }

    private CachedSecret? TryGetCached(string name) => _memory.TryGet(name, out var entry) ? entry : null;

    private CachedSecret ToEntry(string name, SecretResponse? body)
    {
        if (body is null)
            throw new MiniVaultRequestException($"The MiniVault server returned an empty body for secret '{name}'.");

        byte[] value;
        try
        {
            value = Convert.FromBase64String(body.Value ?? "");
        }
        catch (FormatException ex)
        {
            throw new MiniVaultRequestException($"The MiniVault server returned a value for '{name}' that is not valid base64: {ex.Message}");
        }

        return new CachedSecret(name, value, body.ContentType, body.Version, body.UpdatedAt, _now());
    }

    /// <summary>Returns a copy of <paramref name="entry"/> whose <c>FetchedAt</c> is now — the server just confirmed it.</summary>
    private CachedSecret Confirm(CachedSecret entry) =>
        new CachedSecret(entry.Name, entry.Value, entry.ContentType, entry.Version, entry.UpdatedAt, _now());

    private void Invalidate(string name)
    {
        _memory.Remove(name);
        PersistDisk();
    }

    /// <summary>
    /// Writes the whole in-memory snapshot to the disk cache, if one is configured. A disk failure never fails
    /// the operation that triggered it — the cache is an optimization, not the source of truth.
    /// </summary>
    private void PersistDisk()
    {
        if (_disk is null) return;

        try
        {
            _disk.Save(_memory.Snapshot());
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"MiniVault disk cache at '{_disk.FilePath}' could not be written: {ex.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MiniVaultClient));
    }

    private static string EnsureTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
}
