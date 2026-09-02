using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Hosting;
using MiniVault.Server.Keys;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Tests.TestDoubles;

/// <summary>
/// A fully-wired MiniVault service provider (everything AddMiniVaultCore registers) backed by a throw-away
/// LocalDB database, for tests that exercise services such as SecretService, AuditWriter, and ClientDirectory.
/// </summary>
public sealed class VaultFixture : IAsyncDisposable
{
    public TestDatabase Db { get; private init; } = null!;
    public InMemoryMasterKeyProvider Provider { get; private init; } = null!;
    public ServiceProvider ServiceProvider { get; private init; } = null!;

    private VaultFixture() { }

    public static async Task<VaultFixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(migrate: false);
        var provider = new InMemoryMasterKeyProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MiniVault"] = db.ConnectionString,
                ["MasterKey:Provider"] = "Environment",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMiniVaultCore(configuration);
        // Last registration wins for GetRequiredService, so this replaces the Environment provider AddMiniVaultCore just registered.
        services.AddSingleton<IMasterKeyProvider>(provider);
        var serviceProvider = services.BuildServiceProvider();

        try
        {
            await using (var scope = serviceProvider.CreateAsyncScope())
            {
                var initializer = scope.ServiceProvider.GetRequiredService<VaultInitializer>();
                await initializer.InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
            }

            var ring = serviceProvider.GetRequiredService<DataKeyRing>();
            await ring.LoadAsync(CancellationToken.None);
        }
        catch
        {
            // Nothing owns the LocalDB database yet, so a failure here would leak it for the rest of the run.
            await serviceProvider.DisposeAsync();
            await db.DisposeAsync();
            throw;
        }

        return new VaultFixture { Db = db, Provider = provider, ServiceProvider = serviceProvider };
    }

    public IServiceScope CreateScope() => ServiceProvider.CreateScope();

    public async ValueTask DisposeAsync()
    {
        await ServiceProvider.DisposeAsync();
        await Db.DisposeAsync();
    }
}
