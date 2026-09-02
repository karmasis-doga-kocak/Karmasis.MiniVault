using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Tests.Vault;

public class VaultInitializerTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync(migrate: false);
    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Initialize_Single_RandomKey_StoresKeyAndCreatesActiveDek()
    {
        var provider = new InMemoryMasterKeyProvider();
        await using var ctx = _db.CreateContext();
        var sut = new VaultInitializer(ctx, provider, _clock);

        var result = await sut.InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);

        result.MasterKeyStored.ShouldBeTrue();
        result.MasterKeyBase64.ShouldBeNull();
        result.Recovery.Parts.Count.ShouldBe(1);
        provider.Exists().ShouldBeTrue();

        var meta = await ctx.VaultMetadata.SingleAsync();
        meta.RecoveryMode.ShouldBe(RecoveryMode.Single);
        meta.KekSalt.ShouldBeNull();
        meta.InitializedAt.ShouldBe(_clock.GetUtcNow());
        KeyWrapper_Unwrap(meta.RecoveryKeyWrappedByMaster, provider.GetKek()).ShouldBe(result.Recovery.Key);

        var key = await ctx.DataKeys.SingleAsync();
        key.Version.ShouldBe(1);
        key.IsActive.ShouldBeTrue();
        KeyHierarchy.UnwrapWithMaster(key, provider.GetKek()).ShouldBe(KeyHierarchy.UnwrapWithRecovery(key, result.Recovery.Key));

        (await ctx.AuditLogs.SingleAsync()).Action.ShouldBe("init");
    }

    [Fact]
    public async Task Initialize_Shamir_WithPassword_StoresSaltAndIterations()
    {
        var provider = new InMemoryMasterKeyProvider();
        await using var ctx = _db.CreateContext();
        var sut = new VaultInitializer(ctx, provider, _clock);

        var result = await sut.InitializeAsync(new InitOptions(RecoveryMode.Shamir, 3, 2, "P@ssw0rd!"), CancellationToken.None);

        result.Recovery.Parts.Count.ShouldBe(3);
        var meta = await ctx.VaultMetadata.SingleAsync();
        meta.Shares.ShouldBe(3);
        meta.Threshold.ShouldBe(2);
        meta.KekSalt!.Length.ShouldBe(16);
        meta.KekIterations.ShouldBe(Karmasis.Cryptography.Keys.KeyDerivation.DefaultIterations);
        MasterKeyMaterial.FromPassword("P@ssw0rd!", meta.KekSalt!, meta.KekIterations!.Value).Kek.ShouldBe(provider.GetKek());
    }

    [Fact]
    public async Task Initialize_WhenProviderCannotStore_ReturnsKeyForOperator()
    {
        var provider = new NonStoringProvider();
        await using var ctx = _db.CreateContext();
        var sut = new VaultInitializer(ctx, provider, _clock);

        var result = await sut.InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);

        result.MasterKeyStored.ShouldBeFalse();
        Convert.FromBase64String(result.MasterKeyBase64!).Length.ShouldBe(32);
    }

    [Fact]
    public async Task Initialize_Twice_Throws()
    {
        var provider = new InMemoryMasterKeyProvider();
        await using var ctx = _db.CreateContext();
        var sut = new VaultInitializer(ctx, provider, _clock);
        await sut.InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);

        await Should.ThrowAsync<VaultAlreadyInitializedException>(() => sut.InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_WhenStoreFails_DoesNotMarkVaultInitialized()
    {
        var provider = new ThrowingStoreProvider();
        await using var ctx = _db.CreateContext();
        var sut = new VaultInitializer(ctx, provider, _clock);

        await Should.ThrowAsync<IOException>(() => sut.InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None));

        await using var check = _db.CreateContext();
        (await check.VaultMetadata.AnyAsync()).ShouldBeFalse();
        (await check.DataKeys.AnyAsync()).ShouldBeFalse();
    }

    private static byte[] KeyWrapper_Unwrap(byte[] wrapped, byte[] kek) => Karmasis.Cryptography.Keys.KeyWrapper.Unwrap(wrapped, kek);

    private sealed class NonStoringProvider : IMasterKeyProvider
    {
        public string Name => "NonStoring";
        public bool CanStore => false;
        public bool Exists() => false;
        public byte[] GetKek() => throw new MasterKeyUnavailableException("none");
        public void Store(byte[] kek) => throw new NotSupportedException();
    }

    private sealed class ThrowingStoreProvider : IMasterKeyProvider
    {
        public string Name => "ThrowingStore";
        public bool CanStore => true;
        public bool Exists() => false;
        public byte[] GetKek() => throw new MasterKeyUnavailableException("none");
        public void Store(byte[] kek) => throw new IOException("disk full");
    }
}
