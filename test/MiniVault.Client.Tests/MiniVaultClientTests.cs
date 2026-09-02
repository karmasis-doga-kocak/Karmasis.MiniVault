using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MiniVault.Client.Internal;
using MiniVault.Client.Tests.TestDoubles;
using MiniVault.Contracts;

namespace MiniVault.Client.Tests;

public class MiniVaultClientTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private const string ClientId = "client";
    private const string ClientSecret = "secret";

    /// <summary>The shortest background-refresh interval the options allow; used wherever a test needs ticks.</summary>
    private static readonly TimeSpan RefreshTick = TimeSpan.FromSeconds(1);

    /// <summary>How long a test waits for the background timer to do something before giving up.</summary>
    private static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(15);

    private readonly string _dir;

    public MiniVaultClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "minivault-client-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static MiniVaultOptions Options(string? cacheDirectory = null, TimeSpan? refreshInterval = null, string baseUrl = "https://vault.test") =>
        new MiniVaultOptions
        {
            BaseUrl = baseUrl,
            ClientId = ClientId,
            ClientSecret = ClientSecret,
            CacheDirectory = cacheDirectory,
            RefreshInterval = refreshInterval,
        };

    private static StubHandler Token(StubHandler handler) =>
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok", ExpiresIn = 3600 });

    private static StubHandler Secret200(StubHandler handler, string name, string value, int version) =>
        handler.Enqueue(
            HttpStatusCode.OK,
            new SecretResponse
            {
                Name = name,
                Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(value)),
                ContentType = "text/plain",
                Version = version,
                UpdatedAt = T0,
            },
            response => response.Headers.ETag = new EntityTagHeaderValue("\"" + version + "\""));

    private static int TokenRequestCount(StubHandler handler) =>
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/v1/auth/token", StringComparison.Ordinal));

    private static IReadOnlyList<string> IfNoneMatch(HttpRequestMessage request) =>
        request.Headers.TryGetValues("If-None-Match", out var values) ? values.ToList() : Array.Empty<string>();

    private static bool IsTokenRequest(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/v1/auth/token", StringComparison.Ordinal);

    private static HttpResponseMessage TokenResponse200(string accessToken = "tok") =>
        StubHandler.JsonResponse(HttpStatusCode.OK, new TokenResponse { AccessToken = accessToken, ExpiresIn = 3600 });

    private IReadOnlyList<CachedSecret> OnDisk() => new DiskCache(_dir, ClientId, ClientSecret, null).Load();

    /// <summary>Polls <paramref name="condition"/> until it holds, or fails the test when the wait runs out.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < RefreshWait)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new Shouldly.ShouldAssertException($"Timed out after {RefreshWait} waiting until {because}.");
    }

    /// <summary>A handler that fails every request the way an unreachable server does.</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
        }
    }

    [Fact]
    public async Task Get_FetchesAndCaches_ThenUses304()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Enqueue(HttpStatusCode.NotModified);

        using var client = new MiniVaultClient(Options(), handler, () => T0);

        var first = await client.GetSecretAsync("a");
        var second = await client.GetSecretAsync("a");

        first.AsString().ShouldBe("v1");
        second.AsString().ShouldBe("v1");
        second.Version.ShouldBe(1);
        handler.Requests.Count.ShouldBe(3);
        IfNoneMatch(handler.Requests[1]).ShouldBeEmpty();
        IfNoneMatch(handler.Requests[2]).ShouldBe(new[] { "\"1\"" });
    }

    [Fact]
    public async Task Get_ServesDiskCache_WhenServerUnreachable_AndRaisesEvent()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);

        using (var warm = new MiniVaultClient(Options(cacheDirectory: _dir), handler, () => T0))
        {
            (await warm.GetSecretAsync("a")).AsString().ShouldBe("v1");
        }

        var offline = new OfflineHandler();
        using var client = new MiniVaultClient(Options(cacheDirectory: _dir), offline, () => T0);
        CacheServedEventArgs? served = null;
        client.SecretServedFromCache += (_, e) => served = e;

        var secret = await client.GetSecretAsync("a");

        secret.AsString().ShouldBe("v1");
        secret.Version.ShouldBe(1);
        offline.Count.ShouldBeGreaterThan(0);
        served.ShouldNotBeNull();
        served!.Name.ShouldBe("a");
        served.Stale.ShouldBeFalse();
        served.FetchedAt.ShouldBe(T0);
    }

    [Fact]
    public async Task Get_MarksStale_WhenOlderThanMaxCacheAge()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);

        using (var warm = new MiniVaultClient(Options(cacheDirectory: _dir), handler, () => T0))
        {
            await warm.GetSecretAsync("a");
        }

        var later = T0.AddDays(8);
        using var client = new MiniVaultClient(Options(cacheDirectory: _dir), new OfflineHandler(), () => later);
        CacheServedEventArgs? served = null;
        client.SecretServedFromCache += (_, e) => served = e;

        (await client.GetSecretAsync("a")).AsString().ShouldBe("v1");

        served.ShouldNotBeNull();
        served!.Stale.ShouldBeTrue();
        served.FetchedAt.ShouldBe(T0);
    }

    [Fact]
    public async Task Get_WithoutCache_ThrowsUnavailable()
    {
        using var client = new MiniVaultClient(Options(cacheDirectory: _dir), new OfflineHandler(), () => T0);
        var raised = 0;
        client.SecretServedFromCache += (_, _) => raised++;

        await Should.ThrowAsync<MiniVaultUnavailableException>(() => client.GetSecretAsync("a"));

        raised.ShouldBe(0);
    }

    [Fact]
    public async Task Get_401_RefreshesTokenOnce_ThenSucceeds()
    {
        var handler = new StubHandler();
        Token(handler);
        handler.Enqueue(HttpStatusCode.Unauthorized, new ErrorResponse { Error = ErrorResponse.Unauthorized });
        Token(handler);
        Secret200(handler, "a", "v1", 1);

        using var client = new MiniVaultClient(Options(), handler, () => T0);

        var secret = await client.GetSecretAsync("a");

        secret.AsString().ShouldBe("v1");
        TokenRequestCount(handler).ShouldBe(2);
        handler.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Get_401Twice_ThrowsAuth()
    {
        var handler = new StubHandler();
        Token(handler);
        handler.Enqueue(HttpStatusCode.Unauthorized, new ErrorResponse { Error = ErrorResponse.Unauthorized });
        Token(handler);
        handler.Enqueue(HttpStatusCode.Unauthorized, new ErrorResponse { Error = ErrorResponse.Unauthorized });

        using var client = new MiniVaultClient(Options(), handler, () => T0);

        await Should.ThrowAsync<MiniVaultAuthException>(() => client.GetSecretAsync("a"));

        TokenRequestCount(handler).ShouldBe(2);
        handler.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Get_403_DoesNotFallBackToCache()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Enqueue(HttpStatusCode.Forbidden, new ErrorResponse { Error = ErrorResponse.Forbidden });

        using var client = new MiniVaultClient(Options(cacheDirectory: _dir), handler, () => T0);
        var raised = 0;
        client.SecretServedFromCache += (_, _) => raised++;

        await client.GetSecretAsync("a");
        await Should.ThrowAsync<MiniVaultForbiddenException>(() => client.GetSecretAsync("a"));

        raised.ShouldBe(0);
        // A 403 never invalidates the cache — the disk copy from the successful first read is still there.
        new DiskCache(_dir, ClientId, ClientSecret, null).Load().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Get_ServesDiskCache_HandlerThrows_DoesNotBreakResult()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);

        using (var warm = new MiniVaultClient(Options(cacheDirectory: _dir), handler, () => T0))
        {
            (await warm.GetSecretAsync("a")).AsString().ShouldBe("v1");
        }

        var logs = new List<string>();
        var options = Options(cacheDirectory: _dir);
        options.Log = logs.Add;

        using var client = new MiniVaultClient(options, new OfflineHandler(), () => T0);
        client.SecretServedFromCache += (_, _) => throw new InvalidOperationException("boom");

        var secret = await client.GetSecretAsync("a");

        secret.AsString().ShouldBe("v1");
        logs.ShouldContain(l => l.Contains("SecretServedFromCache handler threw"));
    }

    [Fact]
    public async Task Set_CachesTheWrittenValue_AndReturnsVersion()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Enqueue(HttpStatusCode.OK, new SetSecretResponse { Version = 2 });
        handler.Enqueue(HttpStatusCode.NotModified);

        using var client = new MiniVaultClient(Options(cacheDirectory: _dir), handler, () => T0);

        await client.GetSecretAsync("a");
        var version = await client.SetSecretAsync("a", Encoding.UTF8.GetBytes("v2"), "text/plain");

        version.ShouldBe(2);

        // The written value replaces the previous one in both caches instead of being dropped.
        var onDisk = OnDisk();
        onDisk.Count.ShouldBe(1);
        onDisk[0].Version.ShouldBe(2);
        onDisk[0].Value.ShouldBe(Encoding.UTF8.GetBytes("v2"));

        // So the next read is conditional on the version just written, and a 304 confirms it.
        (await client.GetSecretAsync("a")).AsString().ShouldBe("v2");
        IfNoneMatch(handler.Requests[3]).ShouldBe(new[] { "\"2\"" });
    }

    [Fact]
    public async Task Set_LeavesTheWrittenValue_OnDisk_ForAnOfflineRestart()
    {
        var handler = new StubHandler();
        Token(handler);
        handler.Enqueue(HttpStatusCode.OK, new SetSecretResponse { Version = 4 });

        using (var writer = new MiniVaultClient(Options(cacheDirectory: _dir), handler, () => T0))
        {
            (await writer.SetSecretAsync("a", Encoding.UTF8.GetBytes("written"), "text/plain")).ShouldBe(4);
        }

        // A brand-new client, same cache directory, with nothing reachable behind it.
        var offline = new OfflineHandler();
        using var restarted = new MiniVaultClient(Options(cacheDirectory: _dir), offline, () => T0);

        var secret = await restarted.GetSecretAsync("a");

        secret.AsString().ShouldBe("written");
        secret.Version.ShouldBe(4);
        secret.ContentType.ShouldBe("text/plain");
    }

    [Fact]
    public async Task Set_ThenGet_WithRefreshInterval_ReturnsWrittenValue_WithoutARequest()
    {
        var handler = new StubHandler();
        Token(handler);
        handler.Enqueue(HttpStatusCode.OK, new SetSecretResponse { Version = 2 });

        using var client = new MiniVaultClient(Options(refreshInterval: TimeSpan.FromMinutes(30)), handler, () => T0);

        await client.SetSecretAsync("a", Encoding.UTF8.GetBytes("v2"), "text/plain");
        var before = handler.Requests.Count;

        var secret = await client.GetSecretAsync("a");

        secret.AsString().ShouldBe("v2");
        secret.Version.ShouldBe(2);
        handler.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task Delete_InvalidatesCache()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Enqueue(HttpStatusCode.NoContent);
        Secret200(handler, "a", "v3", 3);

        using var client = new MiniVaultClient(Options(cacheDirectory: _dir), handler, () => T0);

        await client.GetSecretAsync("a");
        await client.DeleteSecretAsync("a");

        new DiskCache(_dir, ClientId, ClientSecret, null).Load().ShouldBeEmpty();

        (await client.GetSecretAsync("a")).AsString().ShouldBe("v3");
        IfNoneMatch(handler.Requests[3]).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_ReturnsItems()
    {
        var handler = new StubHandler();
        Token(handler);
        handler.Enqueue(HttpStatusCode.OK, new List<SecretListItem>
        {
            new SecretListItem { Name = "db/password", Version = 4, UpdatedAt = T0 },
        });

        using var client = new MiniVaultClient(Options(), handler, () => T0);

        var items = await client.ListSecretsAsync("db/");

        items.Count.ShouldBe(1);
        items[0].Name.ShouldBe("db/password");
        handler.Requests[1].RequestUri!.AbsolutePath.ShouldBe("/v1/secrets");
    }

    [Fact]
    public async Task RefreshInterval_ReturnsMemory_WithoutRequest()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);

        using var client = new MiniVaultClient(Options(refreshInterval: TimeSpan.FromMinutes(30)), handler, () => T0);

        await client.GetSecretAsync("a");
        var before = handler.Requests.Count;
        var second = await client.GetSecretAsync("a");

        second.AsString().ShouldBe("v1");
        handler.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task RefreshInterval_StaleEntry_ServerReachable_RefreshesLive_WithoutEvent()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        Token(handler); // the first token has expired by the time the clock has moved eight days on
        handler.Enqueue(HttpStatusCode.NotModified);

        var now = T0;
        using var client = new MiniVaultClient(Options(refreshInterval: TimeSpan.FromMinutes(30)), handler, () => now);

        await client.GetSecretAsync("a");

        now = T0.AddDays(8);
        var raised = 0;
        client.SecretServedFromCache += (_, _) => raised++;

        var before = handler.Requests.Count;
        var secret = await client.GetSecretAsync("a");

        // An entry past MaxCacheAge is not served silently from memory: the read goes to the server, and the
        // server is up, so what comes back is a confirmed live read — no cache event at all.
        secret.AsString().ShouldBe("v1");
        handler.Requests.Count.ShouldBeGreaterThan(before);
        IfNoneMatch(handler.Requests[handler.Requests.Count - 1]).ShouldBe(new[] { "\"1\"" });
        raised.ShouldBe(0);
        handler.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task RefreshInterval_StaleEntry_ServerUnreachable_RaisesStaleEvent()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Fallback = _ => throw new HttpRequestException("offline");

        var now = T0;
        using var client = new MiniVaultClient(Options(refreshInterval: TimeSpan.FromMinutes(30)), handler, () => now);

        await client.GetSecretAsync("a");

        now = T0.AddDays(8);
        CacheServedEventArgs? served = null;
        client.SecretServedFromCache += (_, e) => served = e;

        var secret = await client.GetSecretAsync("a");

        secret.AsString().ShouldBe("v1");
        served.ShouldNotBeNull();
        served!.Name.ShouldBe("a");
        served.Stale.ShouldBeTrue();
        served.FetchedAt.ShouldBe(T0);
    }

    [Fact]
    public async Task BackgroundRefresh_UpdatesChangedSecret()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Enqueue(HttpStatusCode.NotModified);
        Secret200(handler, "a", "v2", 2);

        var v2Dequeued = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.OnResponse = response =>
        {
            if (response.Headers.ETag?.Tag == "\"2\"") v2Dequeued.TrySetResult(true);
        };

        using var client = new MiniVaultClient(Options(refreshInterval: RefreshTick), handler, () => T0);

        (await client.GetSecretAsync("a")).Version.ShouldBe(1);

        var signalled = await Task.WhenAny(v2Dequeued.Task, Task.Delay(RefreshWait));
        signalled.ShouldBe(v2Dequeued.Task);

        Secret? latest = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            latest = await client.GetSecretAsync("a");
            if (latest.Version == 2) break;
            await Task.Delay(10);
        }

        latest.ShouldNotBeNull();
        latest!.Version.ShouldBe(2);
        latest.AsString().ShouldBe("v2");
    }

    [Fact]
    public async Task BackgroundRefresh_404_EvictsTheSecretFromMemoryAndDisk()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Fallback = request => IsTokenRequest(request)
            ? TokenResponse200()
            : StubHandler.JsonResponse(HttpStatusCode.NotFound, new ErrorResponse { Error = ErrorResponse.NotFound });

        var logs = new List<string>();
        var options = Options(cacheDirectory: _dir, refreshInterval: RefreshTick);
        options.Log = line => { lock (logs) logs.Add(line); };

        using var client = new MiniVaultClient(options, handler, () => T0);

        await client.GetSecretAsync("a");
        OnDisk().ShouldNotBeEmpty();

        await WaitUntilAsync(() => OnDisk().Count == 0, "the background refresh has dropped the deleted secret from disk");

        // Nothing is left in memory either, so the next read goes to the server and surfaces the 404 rather
        // than handing out a secret the server no longer has.
        await Should.ThrowAsync<MiniVaultNotFoundException>(() => client.GetSecretAsync("a"));
        lock (logs) logs.ShouldContain(l => l.Contains("the server no longer has it"));
    }

    [Fact]
    public async Task BackgroundRefresh_403_EvictsTheSecret_WhenTheGrantIsRevoked()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Fallback = request => IsTokenRequest(request)
            ? TokenResponse200()
            : StubHandler.JsonResponse(HttpStatusCode.Forbidden, new ErrorResponse { Error = ErrorResponse.Forbidden });

        var logs = new List<string>();
        var options = Options(cacheDirectory: _dir, refreshInterval: RefreshTick);
        options.Log = line => { lock (logs) logs.Add(line); };

        using var client = new MiniVaultClient(options, handler, () => T0);

        await client.GetSecretAsync("a");
        OnDisk().ShouldNotBeEmpty();

        await WaitUntilAsync(() => OnDisk().Count == 0, "the background refresh has dropped the forbidden secret from disk");

        await Should.ThrowAsync<MiniVaultForbiddenException>(() => client.GetSecretAsync("a"));
        lock (logs) logs.ShouldContain(l => l.Contains("access to it was revoked"));
    }

    [Fact]
    public async Task BackgroundRefresh_ServerUnreachable_RaisesServedFromCache_ForTheEntryItCouldNotReach()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Fallback = _ => throw new HttpRequestException("offline");

        using var client = new MiniVaultClient(Options(refreshInterval: RefreshTick), handler, () => T0);

        var raised = new TaskCompletionSource<CacheServedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SecretServedFromCache += (_, e) => raised.TrySetResult(e);

        await client.GetSecretAsync("a");

        (await Task.WhenAny(raised.Task, Task.Delay(RefreshWait))).ShouldBe(raised.Task);

        // No call to GetSecretAsync was involved: the refresh pass itself reports what it could not confirm.
        var served = await raised.Task;
        served.Name.ShouldBe("a");
        served.FetchedAt.ShouldBe(T0);
        served.Stale.ShouldBeFalse();
    }

    [Fact]
    public async Task BackgroundRefresh_ServerUnreachable_ReportsStale_OnceThePassIsPastMaxCacheAge()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Fallback = _ => throw new HttpRequestException("offline");

        var now = T0;
        using var client = new MiniVaultClient(Options(refreshInterval: RefreshTick), handler, () => now);

        await client.GetSecretAsync("a");
        now = T0.AddDays(8);

        var raised = new TaskCompletionSource<CacheServedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SecretServedFromCache += (_, e) => raised.TrySetResult(e);

        (await Task.WhenAny(raised.Task, Task.Delay(RefreshWait))).ShouldBe(raised.Task);

        var served = await raised.Task;
        served.Name.ShouldBe("a");
        served.Stale.ShouldBeTrue();
        served.FetchedAt.ShouldBe(T0);
    }

    [Fact]
    public async Task Dispose_StopsTimer()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);

        var client = new MiniVaultClient(Options(refreshInterval: RefreshTick), handler, () => T0);
        await client.GetSecretAsync("a");

        client.Dispose();
        // The window we assert over starts only after Dispose() has returned, so a tick already in flight when
        // Dispose was called cannot be mistaken for the timer still running afterwards.
        var after = handler.Requests.Count;

        // Comfortably longer than two refresh intervals, so a timer that was still running would show up.
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        handler.Requests.Count.ShouldBe(after);
    }

    [Fact]
    public async Task Get_TwoCallersHoldingTheSameStaleToken_CauseExactlyOneReLogin()
    {
        var handler = new StaleTokenHandler(callers: 2);

        using var client = new MiniVaultClient(Options(), handler, () => T0);

        var first = client.GetSecretAsync("a");
        var second = client.GetSecretAsync("b");
        var secrets = await Task.WhenAll(first, second);

        secrets.Select(s => s.AsString()).OrderBy(v => v, StringComparer.Ordinal).ShouldBe(new[] { "value-a", "value-b" });

        // One login for the stale token, one for the replacement — not one per caller that saw the 401.
        handler.Logins.ShouldBe(2);
        handler.Unauthorized.ShouldBe(2);
    }

    /// <summary>
    /// Issues a first ("stale") token, answers 401 to every request that presents it — holding each such
    /// request until all of the callers have arrived, so they all observe the failure while still holding the
    /// same token — and answers 200 to anything presenting a later token.
    /// </summary>
    private sealed class StaleTokenHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _allArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _callers;
        private int _logins;
        private int _arrived;
        private int _unauthorized;

        public StaleTokenHandler(int callers) { _callers = callers; }

        public int Logins => Volatile.Read(ref _logins);
        public int Unauthorized => Volatile.Read(ref _unauthorized);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (IsTokenRequest(request))
            {
                var login = Interlocked.Increment(ref _logins);
                return TokenResponse200(login == 1 ? "stale" : "fresh-" + login);
            }

            var name = request.RequestUri!.AbsolutePath.Substring("/v1/secrets/".Length);

            if (request.Headers.Authorization!.Parameter == "stale")
            {
                if (Interlocked.Increment(ref _arrived) == _callers) _allArrived.TrySetResult(true);
                await _allArrived.Task.ConfigureAwait(false);

                Interlocked.Increment(ref _unauthorized);
                return StubHandler.JsonResponse(HttpStatusCode.Unauthorized, new ErrorResponse { Error = ErrorResponse.Unauthorized });
            }

            return StubHandler.JsonResponse(
                HttpStatusCode.OK,
                new SecretResponse
                {
                    Name = name,
                    Value = Convert.ToBase64String(Encoding.UTF8.GetBytes("value-" + name)),
                    ContentType = "text/plain",
                    Version = 1,
                    UpdatedAt = T0,
                },
                response => response.Headers.ETag = new EntityTagHeaderValue("\"1\""));
        }
    }

    [Fact]
    public async Task HttpClient_BaseAddress_HasTrailingSlash()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);

        using var client = new MiniVaultClient(Options(baseUrl: "https://vault.test"), handler, () => T0);
        client.BaseAddress!.ToString().ShouldBe("https://vault.test/");

        await client.GetSecretAsync("a");

        handler.Requests[1].RequestUri!.ToString().ShouldBe("https://vault.test/v1/secrets/a");
    }

    [Fact]
    public void Factory_Validates_Options()
    {
        Should.Throw<ArgumentException>(() => MiniVaultClientFactory.Create(new MiniVaultOptions()));
        Should.Throw<ArgumentException>(() => MiniVaultClientFactory.Create(new MiniVaultOptions(), new StubHandler()));
        Should.Throw<ArgumentException>(() => MiniVaultClientFactory.Create(Options(baseUrl: "http://vault.test")));
        Should.Throw<ArgumentNullException>(() => MiniVaultClientFactory.Create(null!));
    }

    [Fact]
    public void Factory_CreateHandler_SetsCallback_OnlyWhenThumbprintGiven()
    {
        using var plain = MiniVaultClientFactory.CreateHandler(Options());
        plain.ServerCertificateCustomValidationCallback.ShouldBeNull();

        var pinned = Options();
        pinned.ServerCertificateThumbprint = string.Join(":", Enumerable.Repeat("AB", 20));
        using var handler = MiniVaultClientFactory.CreateHandler(pinned);
        handler.ServerCertificateCustomValidationCallback.ShouldNotBeNull();
    }

    [Fact]
    public void Factory_CreateHandler_Throws_WhenThumbprintDoesNotNormalizeTo40HexChars()
    {
        var options = Options();
        options.ServerCertificateThumbprint = "::";
        Should.Throw<ArgumentException>(() => MiniVaultClientFactory.CreateHandler(options));
    }

    [Fact]
    public void NormalizeThumbprint_KeepsOnlyHexDigits_UpperCased()
    {
        // U+200E LEFT-TO-RIGHT MARK, as the Windows certificate MMC prepends when a thumbprint is copied.
        MiniVaultClientFactory.NormalizeThumbprint("‎ab:cd-ef 01").ShouldBe("ABCDEF01");
        MiniVaultClientFactory.NormalizeThumbprint("aa:bb:cc:dd").ShouldBe("AABBCCDD");
        MiniVaultClientFactory.NormalizeThumbprint("  ").ShouldBe("");
    }

#if NET10_0
    [Fact]
    public void Factory_PinningCallback_RejectsDifferentThumbprint_AcceptsMatching()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=minivault-pinning-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var matching = Options();
        // Deliberately mangled formatting: pinning compares case-insensitively and ignores ':' and spaces.
        matching.ServerCertificateThumbprint = string.Join(":", certificate.Thumbprint.ToLowerInvariant()
            .Select((c, i) => new { c, i })
            .GroupBy(x => x.i / 2)
            .Select(g => new string(g.Select(x => x.c).ToArray())));

        using var matchingHandler = MiniVaultClientFactory.CreateHandler(matching);
        var accepts = matchingHandler.ServerCertificateCustomValidationCallback!;
        accepts(null!, certificate, null, SslPolicyErrors.None).ShouldBeTrue();

        var different = Options();
        different.ServerCertificateThumbprint = new string('A', 40);

        using var differentHandler = MiniVaultClientFactory.CreateHandler(different);
        var rejects = differentHandler.ServerCertificateCustomValidationCallback!;
        rejects(null!, certificate, null, SslPolicyErrors.None).ShouldBeFalse();
        rejects(null!, null, null, SslPolicyErrors.None).ShouldBeFalse();
    }
#endif
}
