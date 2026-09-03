using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Karmasis.MiniVault.Contracts;
using Karmasis.MiniVault.Server.Data.Entities;
using Karmasis.MiniVault.Server.Keys;
using Karmasis.MiniVault.Server.Tests.TestDoubles;
using Karmasis.MiniVault.Server.Vault;

namespace Karmasis.MiniVault.Server.Tests;

public class HealthEndpointTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private readonly InMemoryMasterKeyProvider _provider = new();

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(migrate: false);
        await using var ctx = _db.CreateContext();
        await new VaultInitializer(ctx, _provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
    }
    public async Task DisposeAsync() => await _db.DisposeAsync();

    private WebApplicationFactory<Program> CreateFactory(IMasterKeyProvider provider) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:MiniVault", _db.ConnectionString);
            b.UseSetting("Tls:AllowDevelopmentCertificate", "true");
            b.ConfigureTestServices(s => s.AddSingleton(provider));
        });

    [Fact]
    public async Task Health_ReturnsOk_WhenVaultInitialized()
    {
        using var factory = CreateFactory(_provider);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("\"status\":\"ok\"");
    }

    [Fact]
    public async Task Health_ReportsInitializedAndActiveVersion()
    {
        using var factory = CreateFactory(_provider);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        body!.Status.ShouldBe("ok");
        body.Initialized.ShouldBeTrue();
        body.ActiveDataKeyVersion.ShouldBe(1);
    }

    [Fact]
    public void Serve_RefusesToStart_WithWrongMasterKey()
    {
        using var factory = CreateFactory(new InMemoryMasterKeyProvider(new byte[32]));

        Should.Throw<VaultException>(() => factory.CreateClient());
    }
}
