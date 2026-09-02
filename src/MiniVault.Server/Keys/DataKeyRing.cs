using System.Security.Cryptography;
using System.Text;
using Karmasis.Cryptography.Keys;
using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Keys;

/// <summary>
/// Unwrapped DEKs for the running server. Loaded at startup; reloaded once when a caller asks for a version
/// that is not in memory (another process ran rotate-dek). Readers see an immutable snapshot; GetDek returns copies.
/// Also derives the JWT signing key from the KEK (HKDF) so no separate signing secret exists.
/// </summary>
public sealed class DataKeyRing(IServiceScopeFactory scopes, IMasterKeyProvider provider)
{
    private sealed record Snapshot(IReadOnlyDictionary<int, byte[]> Deks, int ActiveVersion, byte[] JwtKey);

    private static readonly byte[] JwtSalt = Encoding.UTF8.GetBytes("jwt");
    private readonly SemaphoreSlim _reload = new(1, 1);
    private volatile Snapshot? _snapshot;

    public bool IsLoaded => _snapshot is not null;
    public int ActiveVersion => Current().ActiveVersion;
    public byte[] ActiveDek => GetActive().Dek;
    public byte[] JwtSigningKey => (byte[])Current().JwtKey.Clone();

    /// <summary>The active version and its DEK from a single snapshot read, so a concurrent reload cannot
    /// pair a version number with a different version's key.</summary>
    public (int Version, byte[] Dek) GetActive()
    {
        var snap = Current();
        return (snap.ActiveVersion, Copy(snap, snap.ActiveVersion));
    }

    public byte[] GetDek(int version) => Copy(Current(), version);

    /// <summary>Returns the DEK for a version, reloading from the database once if it is unknown.</summary>
    public async Task<byte[]> GetDekAsync(int version, CancellationToken ct)
    {
        var snap = Current();
        if (snap.Deks.TryGetValue(version, out var dek)) return (byte[])dek.Clone();
        await ReloadAsync(ct);
        return GetDek(version);
    }

    public Task LoadAsync(CancellationToken ct) => ReloadAsync(ct);

    public async Task ReloadAsync(CancellationToken ct)
    {
        await _reload.WaitAsync(ct);
        try
        {
            _snapshot = await BuildSnapshotAsync(ct);
        }
        finally
        {
            _reload.Release();
        }
    }

    private Snapshot Current() => _snapshot ?? throw new VaultNotInitializedException();

    private static byte[] Copy(Snapshot snap, int version) =>
        snap.Deks.TryGetValue(version, out var dek) ? (byte[])dek.Clone() : throw new UnknownDataKeyException(version);

    private async Task<Snapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MiniVaultDbContext>();

        if (!await db.VaultMetadata.AnyAsync(ct)) throw new VaultNotInitializedException();
        var keys = await db.DataKeys.AsNoTracking().ToListAsync(ct);
        var activeKeys = keys.Where(k => k.IsActive).ToList();
        if (activeKeys.Count != 1) throw new VaultException($"Expected exactly one active data key, found {activeKeys.Count}.");

        byte[] kek;
        try { kek = provider.GetKek(); }
        catch (MasterKeyUnavailableException ex) { throw new VaultException($"Master key unavailable ({provider.Name}): {ex.Message}", ex); }

        var deks = new Dictionary<int, byte[]>();
        try
        {
            foreach (var key in keys)
                deks[key.Version] = KeyHierarchy.UnwrapWithMaster(key, kek);
            var jwtKey = KeyDerivation.Hkdf(kek, JwtSalt, "minivault-jwt", 32);
            return new Snapshot(deks, activeKeys[0].Version, jwtKey);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            foreach (var d in deks.Values) Array.Clear(d);
            throw new VaultException("The master key does not unwrap the stored data keys. Wrong master key for this database, or the database belongs to another vault.", ex);
        }
        finally
        {
            Array.Clear(kek);
        }
    }
}
