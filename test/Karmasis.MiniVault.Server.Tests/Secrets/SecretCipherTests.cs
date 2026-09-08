using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Karmasis.MiniVault.Server.Data.Entities;
using Karmasis.MiniVault.Server.Keys;
using Karmasis.MiniVault.Server.Secrets;
using Karmasis.MiniVault.Server.Tests.TestDoubles;
using Karmasis.MiniVault.Server.Vault;
using Microsoft.EntityFrameworkCore;
using Karmasis.MiniVault.Server.Data;

namespace Karmasis.MiniVault.Server.Tests.Secrets;

public class SecretCipherTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private readonly InMemoryMasterKeyProvider _provider = new();
    private ServiceProvider _sp = null!;
    private DataKeyRing _ring = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(migrate: false);
        await using (var ctx = _db.CreateContext())
            await new VaultInitializer(ctx, _provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
        var services = new ServiceCollection();
        services.AddDbContext<MiniVaultDbContext>(o => o.UseSqlServer(_db.ConnectionString));
        services.AddSingleton<IMasterKeyProvider>(_provider);
        services.AddSingleton<DataKeyRing>();
        _sp = services.BuildServiceProvider();
        _ring = _sp.GetRequiredService<DataKeyRing>();
        await _ring.LoadAsync(CancellationToken.None);
    }
    public async Task DisposeAsync() { await _sp.DisposeAsync(); await _db.DisposeAsync(); }

    [Fact]
    public async Task Encrypt_ThenDecrypt_RoundTrips_WithActiveVersion()
    {
        var cipher = new SecretCipher(_ring);
        var value = "Server=x;Password=y"u8.ToArray();

        var (blob, version) = cipher.Encrypt("dataskope/conn", value);

        version.ShouldBe(1);
        blob.ShouldNotBe(value);
        (await cipher.DecryptAsync("dataskope/conn", blob, version, CancellationToken.None)).ShouldBe(value);
    }

    [Fact]
    public async Task Decrypt_WithDifferentName_Fails()
    {
        var cipher = new SecretCipher(_ring);
        var (blob, version) = cipher.Encrypt("a", [1, 2, 3]);

        await Should.ThrowAsync<CryptographicException>(() => cipher.DecryptAsync("b", blob, version, CancellationToken.None));
    }

    [Fact]
    public async Task Decrypt_OldVersion_StillWorksAfterRotation()
    {
        var cipher = new SecretCipher(_ring);
        var (blob, version) = cipher.Encrypt("a", [9]);
        await using (var ctx = _db.CreateContext())
            await new VaultRecovery(ctx, _provider, TimeProvider.System).RotateDekAsync(CancellationToken.None);

        (await cipher.DecryptAsync("a", blob, version, CancellationToken.None)).ShouldBe(new byte[] { 9 });
        cipher.Encrypt("a", [9]).DekVersion.ShouldBe(1);

        // The ring only reloads on a version miss (or restart); an explicit reload models that.
        await _ring.ReloadAsync(CancellationToken.None);
        var (_, newVersion) = cipher.Encrypt("a", [9]);
        newVersion.ShouldBe(2);
    }
}
