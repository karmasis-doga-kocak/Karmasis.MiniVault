using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Keys;

/// <summary>Unwrapped DEKs for the running server, loaded once at startup with the KEK from the provider.
/// GetDek returns a copy; callers may clear it after use.</summary>
public sealed class DataKeyRing(IServiceScopeFactory scopes, IMasterKeyProvider provider)
{
    private readonly Dictionary<int, byte[]> _deks = new();
    private int _activeVersion;

    public bool IsLoaded { get; private set; }
    public int ActiveVersion => IsLoaded ? _activeVersion : throw new VaultNotInitializedException();
    public byte[] ActiveDek => GetDek(ActiveVersion);

    public byte[] GetDek(int version)
    {
        if (!IsLoaded) throw new VaultNotInitializedException();
        return _deks.TryGetValue(version, out var dek) ? (byte[])dek.Clone() : throw new KeyNotFoundException($"No data key with version {version}.");
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MiniVaultDbContext>();

        if (!await db.VaultMetadata.AnyAsync(ct)) throw new VaultNotInitializedException();
        var keys = await db.DataKeys.AsNoTracking().ToListAsync(ct);
        var active = keys.SingleOrDefault(k => k.IsActive) ?? throw new VaultException("No active data key found.");

        byte[] kek;
        try { kek = provider.GetKek(); }
        catch (MasterKeyUnavailableException ex) { throw new VaultException($"Master key unavailable ({provider.Name}): {ex.Message}", ex); }

        try
        {
            _deks.Clear();
            foreach (var key in keys)
                _deks[key.Version] = KeyHierarchy.UnwrapWithMaster(key, kek);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _deks.Clear();
            throw new VaultException("The master key does not unwrap the stored data keys. Wrong master key for this database, or the database belongs to another vault.", ex);
        }
        finally
        {
            Array.Clear(kek);
        }

        _activeVersion = active.Version;
        IsLoaded = true;
    }
}
