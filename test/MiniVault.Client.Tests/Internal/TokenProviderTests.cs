using System.Net;
using System.Net.Http;
using MiniVault.Client.Internal;
using MiniVault.Client.Tests.TestDoubles;
using MiniVault.Contracts;

namespace MiniVault.Client.Tests.Internal;

public class TokenProviderTests
{
    private static (TokenProvider Provider, StubHandler Handler, TestClock Clock) CreateSut(string clientId = "client", string clientSecret = "secret")
    {
        var handler = new StubHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://vault.test/") };
        var http = new MiniVaultHttp(httpClient);
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var provider = new TokenProvider(http, clientId, clientSecret, clock.Now);
        return (provider, handler, clock);
    }

    private sealed class TestClock
    {
        public DateTimeOffset Value;
        public TestClock(DateTimeOffset initial) { Value = initial; }
        public DateTimeOffset Now() => Value;
    }

    [Fact]
    public async Task GetAsync_FirstCall_LogsIn_AndSendsClientIdAndSecret()
    {
        var (provider, handler, _) = CreateSut(clientId: "my-client", clientSecret: "my-secret");
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok1", ExpiresIn = 3600 });

        var token = await provider.GetAsync(CancellationToken.None);

        token.ShouldBe("tok1");
        handler.Requests.Count.ShouldBe(1);
        var body = handler.RequestBodies[0]!;
        body.ShouldContain("my-client");
        body.ShouldContain("my-secret");
        body.ShouldContain("clientId");
        body.ShouldContain("clientSecret");
    }

    [Fact]
    public async Task GetAsync_SecondCall_WithinLifetime_DoesNotMakeRequest()
    {
        var (provider, handler, clock) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok1", ExpiresIn = 3600 });

        var first = await provider.GetAsync(CancellationToken.None);
        clock.Value = clock.Value.AddSeconds(10);
        var second = await provider.GetAsync(CancellationToken.None);

        second.ShouldBe(first);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_AfterExpiryMargin_LogsInAgain()
    {
        var (provider, handler, clock) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok1", ExpiresIn = 3600 });
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok2", ExpiresIn = 3600 });

        var first = await provider.GetAsync(CancellationToken.None);
        // valid until issuedAt + 3600 - 60 = issuedAt + 3540
        clock.Value = clock.Value.AddSeconds(3541);
        var second = await provider.GetAsync(CancellationToken.None);

        first.ShouldBe("tok1");
        second.ShouldBe("tok2");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Invalidate_ForcesReLogin()
    {
        var (provider, handler, clock) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok1", ExpiresIn = 3600 });
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok2", ExpiresIn = 3600 });

        var first = await provider.GetAsync(CancellationToken.None);
        provider.Invalidate();
        var second = await provider.GetAsync(CancellationToken.None);

        first.ShouldBe("tok1");
        second.ShouldBe("tok2");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_TenConcurrentCalls_ProduceExactlyOneTokenRequest_AllShareToken()
    {
        var (provider, handler, _) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok-shared", ExpiresIn = 3600 });

        var tasks = new Task<string>[10];
        for (var i = 0; i < 10; i++) tasks[i] = provider.GetAsync(CancellationToken.None);
        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(t => t == "tok-shared");
        handler.Requests.Count.ShouldBe(1);
        handler.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task GetAsync_Login401_PropagatesAuthException_AndDoesNotPoisonCache()
    {
        var (provider, handler, _) = CreateSut();
        handler.Enqueue(HttpStatusCode.Unauthorized, new ErrorResponse { Error = ErrorResponse.Unauthorized });
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok-ok", ExpiresIn = 3600 });

        await Should.ThrowAsync<MiniVaultAuthException>(() => provider.GetAsync(CancellationToken.None));

        var token = await provider.GetAsync(CancellationToken.None);

        token.ShouldBe("tok-ok");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_ShortExpiresIn_ValidUntilExpiresIn_NoMarginApplied()
    {
        var (provider, handler, clock) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok1", ExpiresIn = 30 });
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok2", ExpiresIn = 30 });

        var first = await provider.GetAsync(CancellationToken.None);
        clock.Value = clock.Value.AddSeconds(29);
        var stillCached = await provider.GetAsync(CancellationToken.None);

        first.ShouldBe("tok1");
        stillCached.ShouldBe("tok1");
        handler.Requests.Count.ShouldBe(1);

        clock.Value = clock.Value.AddSeconds(2); // total +31s from issue
        var reLoggedIn = await provider.GetAsync(CancellationToken.None);

        reLoggedIn.ShouldBe("tok2");
        handler.Requests.Count.ShouldBe(2);
    }
}
