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
        Should.Throw<UnknownDataKeyException>(() => ring.GetDek(99));
    }

    [Fact]
    public async Task GetDek_ReturnsCopy_CallerMutationDoesNotAffectRing()
    {
        var provider = new InMemoryMasterKeyProvider();
        await using (var ctx = _db.CreateContext())
            await new VaultInitializer(ctx, provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
        await using var sp = BuildServices(provider);
        var ring = sp.GetRequiredService<DataKeyRing>();
        await ring.LoadAsync(CancellationToken.None);

        var first = ring.GetDek(1);
        Array.Clear(first);

        ring.GetDek(1).ShouldNotBe(first);
        ring.GetDek(1).ShouldBe(ring.ActiveDek);
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

    [Fact]
    public async Task GetDekAsync_ReloadsOnMiss_AfterRotation()
    {
        var provider = new InMemoryMasterKeyProvider();
        await using (var ctx = _db.CreateContext())
            await new VaultInitializer(ctx, provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
        await using var sp = BuildServices(provider);
        var ring = sp.GetRequiredService<DataKeyRing>();
        await ring.LoadAsync(CancellationToken.None);
        await using (var ctx = _db.CreateContext())
            await new VaultRecovery(ctx, provider, TimeProvider.System).RotateDekAsync(CancellationToken.None);

        var dek2 = await ring.GetDekAsync(2, CancellationToken.None);

        dek2.Length.ShouldBe(32);
        ring.ActiveVersion.ShouldBe(2);
        await Should.ThrowAsync<UnknownDataKeyException>(() => ring.GetDekAsync(99, CancellationToken.None));
    }

    [Fact]
    public async Task JwtSigningKey_IsStableAcrossReloads_AndIsACopy()
    {
        var provider = new InMemoryMasterKeyProvider();
        await using (var ctx = _db.CreateContext())
            await new VaultInitializer(ctx, provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
        await using var sp = BuildServices(provider);
        var ring = sp.GetRequiredService<DataKeyRing>();
        await ring.LoadAsync(CancellationToken.None);

        var k1 = ring.JwtSigningKey;
        Array.Clear(k1);
        await ring.ReloadAsync(CancellationToken.None);
        var k2 = ring.JwtSigningKey;

        k2.Length.ShouldBe(32);
        k2.ShouldNotBe(new byte[32]);
        ring.JwtSigningKey.ShouldBe(k2);
    }
}
