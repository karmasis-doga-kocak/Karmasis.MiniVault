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
    }

    [Fact]
    public async Task Set_InvalidatesCache_AndReturnsVersion()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Enqueue(HttpStatusCode.OK, new SetSecretResponse { Version = 2 });
        Secret200(handler, "a", "v2", 2);

        using var client = new MiniVaultClient(Options(cacheDirectory: _dir), handler, () => T0);

        await client.GetSecretAsync("a");
        var version = await client.SetSecretAsync("a", Encoding.UTF8.GetBytes("v2"), "text/plain");

        version.ShouldBe(2);
        new DiskCache(_dir, ClientId, ClientSecret, null).Load().ShouldBeEmpty();

        // The cache entry is gone, so the next read is unconditional.
        (await client.GetSecretAsync("a")).AsString().ShouldBe("v2");
        IfNoneMatch(handler.Requests[3]).ShouldBeEmpty();
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
    public async Task BackgroundRefresh_UpdatesChangedSecret()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);
        handler.Enqueue(HttpStatusCode.NotModified);
        Secret200(handler, "a", "v2", 2);

        using var client = new MiniVaultClient(Options(refreshInterval: TimeSpan.FromMilliseconds(50)), handler, () => T0);

        (await client.GetSecretAsync("a")).Version.ShouldBe(1);

        Secret? latest = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            latest = await client.GetSecretAsync("a");
            if (latest.Version == 2) break;
            await Task.Delay(25);
        }

        latest.ShouldNotBeNull();
        latest!.Version.ShouldBe(2);
        latest.AsString().ShouldBe("v2");
    }

    [Fact]
    public async Task Dispose_StopsTimer()
    {
        var handler = new StubHandler();
        Token(handler);
        Secret200(handler, "a", "v1", 1);

        var client = new MiniVaultClient(Options(refreshInterval: TimeSpan.FromMilliseconds(50)), handler, () => T0);
        await client.GetSecretAsync("a");

        client.Dispose();
        await Task.Delay(100);
        var after = handler.Requests.Count;

        await Task.Delay(300);

        handler.Requests.Count.ShouldBe(after);
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
        pinned.ServerCertificateThumbprint = "AA:BB:CC";
        using var handler = MiniVaultClientFactory.CreateHandler(pinned);
        handler.ServerCertificateCustomValidationCallback.ShouldNotBeNull();
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
