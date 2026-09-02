using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Tests.Keys;

public class DataKeyRingTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync(migrate: false);
    public async Task DisposeAsync() => await _db.DisposeAsync();

    private ServiceProvider BuildServices(IMasterKeyProvider provider)
    {
        var services = new ServiceCollection();
        services.AddDbContext<MiniVaultDbContext>(o => o.UseSqlServer(_db.ConnectionString));
        services.AddSingleton(provider);
        services.AddSingleton<DataKeyRing>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Load_AfterInit_ExposesActiveDek()
    {
        var provider = new InMemoryMasterKeyProvider();
        await using (var ctx = _db.CreateContext())
            await new VaultInitializer(ctx, provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
        await using var sp = BuildServices(provider);
        var ring = sp.GetRequiredService<DataKeyRing>();

        await ring.LoadAsync(CancellationToken.None);

        ring.IsLoaded.ShouldBeTrue();
        ring.ActiveVersion.ShouldBe(1);
        ring.ActiveDek.Length.ShouldBe(32);
        ring.GetDek(1).ShouldBe(ring.ActiveDek);
        Should.Throw<KeyNotFoundException>(() => ring.GetDek(99));
    }

    [Fact]
    public async Task Load_OnUninitializedVault_Throws()
    {
        await using (var ctx = _db.CreateContext()) await ctx.Database.MigrateAsync();
        await using var sp = BuildServices(new InMemoryMasterKeyProvider(new byte[32]));
        var ring = sp.GetRequiredService<DataKeyRing>();

        await Should.ThrowAsync<VaultNotInitializedException>(() => ring.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Load_WithWrongKek_Throws()
    {
        var provider = new InMemoryMasterKeyProvider();
        await using (var ctx = _db.CreateContext())
            await new VaultInitializer(ctx, provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
        await using var sp = BuildServices(new InMemoryMasterKeyProvider(new byte[32]));
        var ring = sp.GetRequiredService<DataKeyRing>();

        await Should.ThrowAsync<VaultException>(() => ring.LoadAsync(CancellationToken.None));
    }
}
