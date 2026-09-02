using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Tests;

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
    public void Serve_RefusesToStart_WithWrongMasterKey()
    {
        using var factory = CreateFactory(new InMemoryMasterKeyProvider(new byte[32]));

        Should.Throw<VaultException>(() => factory.CreateClient());
    }
}
