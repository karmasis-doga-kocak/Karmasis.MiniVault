using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniVault.Contracts;
using MiniVault.Server.Auth;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Tests.Api;

/// <summary>
/// A full HTTP-level fixture: a throw-away LocalDB database, an initialized vault, and a WebApplicationFactory
/// wired to it. Seeds two roles (reader: Read on "dataskope/"; admin: Write on the empty scope) and two clients
/// (collector: reader; webui: admin) through ClientDirectory, and remembers their generated secrets so tests can
/// obtain bearer tokens via the real /v1/auth/token endpoint.
/// </summary>
public sealed class ApiTestFixture : IAsyncLifetime
{
    private readonly Dictionary<string, string> _clientSecrets = new();

    public TestDatabase Db { get; private set; } = null!;
    public InMemoryMasterKeyProvider Provider { get; private set; } = null!;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Db = await TestDatabase.CreateAsync(migrate: false);
        Provider = new InMemoryMasterKeyProvider();
        await using (var ctx = Db.CreateContext())
            await new VaultInitializer(ctx, Provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:MiniVault", Db.ConnectionString);
            b.UseSetting("Tls:AllowDevelopmentCertificate", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IMasterKeyProvider>(Provider));
        });

        using var scope = Factory.Services.CreateScope();
        var clients = scope.ServiceProvider.GetRequiredService<ClientDirectory>();

        await clients.AddRoleAsync("reader", null, CancellationToken.None);
        await clients.AddRoleAsync("admin", null, CancellationToken.None);
        await clients.GrantAsync("reader", "dataskope/", Permission.Read, CancellationToken.None);
        await clients.GrantAsync("admin", "", Permission.Write, CancellationToken.None);

        _clientSecrets["collector"] = await clients.AddClientAsync("collector", ["reader"], CancellationToken.None);
        _clientSecrets["webui"] = await clients.AddClientAsync("webui", ["admin"], CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        Factory.Dispose();
        await Db.DisposeAsync();
    }

    /// <summary>The generated client secret for a seeded client, for tests that need to call /v1/auth/token directly.</summary>
    public string SecretFor(string clientId) => _clientSecrets[clientId];

    public async Task<HttpClient> ClientWithTokenAsync(string clientId)
    {
        var http = Factory.CreateClient();
        var response = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = clientId, ClientSecret = _clientSecrets[clientId] });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return http;
    }

    public async Task<List<AuditLog>> AuditAsync()
    {
        await using var ctx = Db.CreateContext();
        return await ctx.AuditLogs.AsNoTracking().ToListAsync();
    }
}
