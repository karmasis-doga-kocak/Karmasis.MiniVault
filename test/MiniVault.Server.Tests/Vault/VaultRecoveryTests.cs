using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Tests.Vault;

public class VaultRecoveryTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private readonly InMemoryMasterKeyProvider _provider = new();
    private InitResult _init = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(migrate: false);
        await using var ctx = _db.CreateContext();
        _init = await new VaultInitializer(ctx, _provider, TimeProvider.System)
            .InitializeAsync(new InitOptions(RecoveryMode.Shamir, 3, 2), CancellationToken.None);
    }
    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Recover_WithThresholdShares_RewrapsDeksUnderNewPassword()
    {
        byte[] originalDek;
        await using (var ctx = _db.CreateContext())
            originalDek = KeyHierarchy.UnwrapWithMaster(await ctx.DataKeys.SingleAsync(), _provider.GetKek());
        var oldKek = _provider.GetKek();

        RecoverResult result;
        await using (var ctx = _db.CreateContext())
            result = await new VaultRecovery(ctx, _provider, TimeProvider.System)
                .RecoverAsync(new RecoverOptions([_init.Recovery.Parts[0], _init.Recovery.Parts[2]], "NewP@ss"), CancellationToken.None);

        result.MasterKeyStored.ShouldBeTrue();
        result.DataKeysRewrapped.ShouldBe(1);
        _provider.GetKek().ShouldNotBe(oldKek);
        await using (var ctx = _db.CreateContext())
        {
            var key = await ctx.DataKeys.SingleAsync();
            KeyHierarchy.UnwrapWithMaster(key, _provider.GetKek()).ShouldBe(originalDek);
            Should.Throw<CryptographicException>(() => KeyHierarchy.UnwrapWithMaster(key, oldKek));
            var meta = await ctx.VaultMetadata.SingleAsync();
            meta.KekSalt.ShouldNotBeNull();
            MasterKeyMaterial.FromPassword("NewP@ss", meta.KekSalt!, meta.KekIterations!.Value).Kek.ShouldBe(_provider.GetKek());
            Karmasis.Cryptography.Keys.KeyWrapper.Unwrap(meta.RecoveryKeyWrappedByMaster, _provider.GetKek()).ShouldBe(_init.Recovery.Key);
            (await ctx.AuditLogs.CountAsync(a => a.Action == "recover")).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Recover_WithRandomNewKey_WorksWithoutPassword()
    {
        await using var ctx = _db.CreateContext();
        var result = await new VaultRecovery(ctx, _provider, TimeProvider.System)
            .RecoverAsync(new RecoverOptions([_init.Recovery.Parts[1], _init.Recovery.Parts[2]], null), CancellationToken.None);

        result.MasterKeyStored.ShouldBeTrue();
        (await ctx.VaultMetadata.SingleAsync()).KekSalt.ShouldBeNull();
    }

    [Fact]
    public async Task Recover_WithWrongShares_Throws()
    {
        var other = RecoveryMaterial.Generate(RecoveryMode.Shamir, 3, 2);
        await using var ctx = _db.CreateContext();

        await Should.ThrowAsync<VaultException>(() => new VaultRecovery(ctx, _provider, TimeProvider.System)
            .RecoverAsync(new RecoverOptions([other.Parts[0], other.Parts[1]], null), CancellationToken.None));
        (await ctx.VaultMetadata.SingleAsync()).KekSalt.ShouldBeNull(); // nothing changed
    }

    [Fact]
    public async Task RotateDek_AddsNewActiveVersion_KeepsOldReadable()
    {
        await using var ctx = _db.CreateContext();
        var sut = new VaultRecovery(ctx, _provider, TimeProvider.System);

        var newVersion = await sut.RotateDekAsync(CancellationToken.None);

        newVersion.ShouldBe(2);
        var keys = await ctx.DataKeys.OrderBy(k => k.Version).ToListAsync();
        keys.Count.ShouldBe(2);
        keys[0].IsActive.ShouldBeFalse();
        keys[1].IsActive.ShouldBeTrue();
        var kek = _provider.GetKek();
        var dek1 = KeyHierarchy.UnwrapWithMaster(keys[0], kek);
        var dek2 = KeyHierarchy.UnwrapWithMaster(keys[1], kek);
        dek1.ShouldNotBe(dek2);
        KeyHierarchy.UnwrapWithRecovery(keys[1], _init.Recovery.Key).ShouldBe(dek2);
        (await ctx.AuditLogs.CountAsync(a => a.Action == "rotate-dek")).ShouldBe(1);
    }

    [Fact]
    public async Task RotateDek_OnUninitializedVault_Throws()
    {
        await using var fresh = await TestDatabase.CreateAsync();
        await using var ctx = fresh.CreateContext();

        await Should.ThrowAsync<VaultNotInitializedException>(() => new VaultRecovery(ctx, _provider, TimeProvider.System).RotateDekAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Recover_WhenStoreFails_ThrowsWithNewKeyInMessage_AndDbIsRewrapped()
    {
        await using var ctx = _db.CreateContext();
        var sut = new VaultRecovery(ctx, new ThrowingStoreProvider(), TimeProvider.System);

        var ex = await Should.ThrowAsync<VaultException>(() => sut.RecoverAsync(new RecoverOptions([_init.Recovery.Parts[0], _init.Recovery.Parts[1]], null), CancellationToken.None));

        ex.Message.ShouldContain("could not be stored");
        var base64 = ex.Message[(ex.Message.LastIndexOf(':') + 1)..].Trim();
        var newKek = Convert.FromBase64String(base64);
        newKek.Length.ShouldBe(32);
        await using var check = _db.CreateContext();
        var key = await check.DataKeys.SingleAsync();
        KeyHierarchy.UnwrapWithMaster(key, newKek).ShouldBe(KeyHierarchy.UnwrapWithRecovery(key, _init.Recovery.Key));
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
