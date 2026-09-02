using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MiniVault.Contracts;
using MiniVault.Server.Auth;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Tests.Api;

public class AuthEndpointTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Token_ValidCredentials_ReturnsToken_AndAudits()
    {
        var http = fixture.Factory.CreateClient();

        var response = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = "webui", ClientSecret = fixture.SecretFor("webui") });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        body!.AccessToken.ShouldNotBeNullOrWhiteSpace();
        body.ExpiresIn.ShouldBe(900);

        var audits = await fixture.AuditAsync();
        audits.ShouldContain(a => a.ClientId == "webui" && a.Action == "token" && a.Success);
    }

    [Fact]
    public async Task Token_WrongSecret_ReturnsUnauthorized_AndAudits()
    {
        var http = fixture.Factory.CreateClient();

        var response = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = "collector", ClientSecret = "wrong-secret" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.Unauthorized);

        var audits = await fixture.AuditAsync();
        audits.ShouldContain(a => a.ClientId == "collector" && a.Action == "token" && !a.Success);
    }

    [Fact]
    public async Task Token_UnknownClient_ReturnsUnauthorized()
    {
        var http = fixture.Factory.CreateClient();

        var response = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = "ghost", ClientSecret = "whatever" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.Unauthorized);
        var audits = await fixture.AuditAsync();
        audits.ShouldContain(a => a.ClientId == "ghost" && a.Action == "token" && !a.Success);
    }

    [Fact]
    public async Task Token_AttemptedClientId_IsSanitizedInAudit()
    {
        var http = fixture.Factory.CreateClient();

        var response = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = "gh<script>ost", ClientSecret = "whatever" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var audits = await fixture.AuditAsync();
        audits.ShouldContain(a => a.Action == "token" && !a.Success && a.ClientId == "ghscriptost");
    }

    [Fact]
    public async Task Token_OverRateLimit_Returns429()
    {
        await using var db = await TestDatabase.CreateAsync(migrate: false);
        var provider = new InMemoryMasterKeyProvider();
        await using (var ctx = db.CreateContext())
            await new VaultInitializer(ctx, provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:MiniVault", db.ConnectionString);
            b.UseSetting("Token:LoginRateLimitPerMinute", "5");
            b.UseSetting("Tls:AllowDevelopmentCertificate", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IMasterKeyProvider>(provider));
        });

        var http = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var attempt = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = "ghost", ClientSecret = "whatever" });
            statuses.Add(attempt.StatusCode);
        }

        statuses.Take(5).ShouldAllBe(status => status == HttpStatusCode.Unauthorized);
        statuses[5].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Token_EmptyBody_ReturnsBadRequest()
    {
        var http = fixture.Factory.CreateClient();

        var response = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = "", ClientSecret = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Token_MalformedJsonBody_400()
    {
        var http = fixture.Factory.CreateClient();
        var content = new StringContent("{\"clientId\":", System.Text.Encoding.UTF8, "application/json");

        var response = await http.PostAsync("/v1/auth/token", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.InvalidRequest);
    }

    [Fact]
    public async Task Expired_Token_ReturnsUnauthorized()
    {
        await using var db = await TestDatabase.CreateAsync(migrate: false);
        var provider = new InMemoryMasterKeyProvider();
        await using (var ctx = db.CreateContext())
            await new VaultInitializer(ctx, provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);

        // Issue tokens from a clock that is already 10 minutes behind real time, with a 1-minute lifetime: the
        // resulting exp claim is ~9 minutes in the past by the real clock the JWT handler validates against.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-10));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:MiniVault", db.ConnectionString);
            b.UseSetting("Token:LifetimeMinutes", "1");
            b.UseSetting("Tls:AllowDevelopmentCertificate", "true");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IMasterKeyProvider>(provider);
                s.AddSingleton<TimeProvider>(clock);
            });
        });

        string secret;
        using (var scope = factory.Services.CreateScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
            secret = await clients.AddClientAsync("expiring-client", [], CancellationToken.None);
        }

        var http = factory.CreateClient();
        var tokenResponse = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = "expiring-client", ClientSecret = secret });
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.GetAsync("/v1/secrets/dataskope/x");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.Unauthorized);
    }
}
