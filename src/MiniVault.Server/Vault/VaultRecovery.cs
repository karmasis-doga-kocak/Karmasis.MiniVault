using System.Security.Cryptography;
using Karmasis.Cryptography.Keys;
using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Vault;

public sealed class VaultRecovery(MiniVaultDbContext db, IMasterKeyProvider provider, TimeProvider clock)
{
    /// <summary>Replaces the master key using the recovery key: every DEK and the stored recovery key are rewrapped. Secrets are untouched.</summary>
    public async Task<RecoverResult> RecoverAsync(RecoverOptions options, CancellationToken ct)
    {
        var meta = await db.VaultMetadata.SingleOrDefaultAsync(ct) ?? throw new VaultNotInitializedException();

        byte[] recoveryKey;
        try { recoveryKey = RecoveryMaterial.Reconstruct(meta.RecoveryMode, options.RecoveryParts); }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        { throw new VaultException($"Invalid recovery input: {ex.Message}", ex); }

        var keys = await db.DataKeys.ToListAsync(ct);
        var master = options.NewMasterKeyPassword is null
            ? MasterKeyMaterial.Random()
            : MasterKeyMaterial.FromPassword(options.NewMasterKeyPassword);

        try
        {
            try
            {
                foreach (var key in keys)
                {
                    var dek = KeyHierarchy.UnwrapWithRecovery(key, recoveryKey);
                    KeyHierarchy.RewrapWithMaster(key, dek, master.Kek);
                    Array.Clear(dek);
                }
                meta.RecoveryKeyWrappedByMaster = KeyWrapper.Wrap(recoveryKey, master.Kek);
            }
            catch (CryptographicException ex)
            {
                throw new VaultException("The recovery key does not unwrap the stored data keys. Wrong recovery key or shares.", ex);
            }
            finally
            {
                Array.Clear(recoveryKey);
            }

            meta.KekSalt = master.Salt;
            meta.KekIterations = master.Iterations;
            db.AuditLogs.Add(new AuditLog { Timestamp = clock.GetUtcNow(), ClientId = VaultInitializer.AuditClientId, Action = "recover", Success = true, Detail = $"rewrapped={keys.Count}" });

            // The database is rewrapped under the new KEK first, then the KEK is stored. If storing fails, the operator
            // receives the new KEK in the error so the vault is never left rewrapped under a key nobody holds; the
            // recovery material stays valid either way because WrappedByRecovery is untouched.
            var kekBase64 = Convert.ToBase64String(master.Kek);
            await db.SaveChangesAsync(ct);
            if (!provider.CanStore)
                return new RecoverResult(false, kekBase64, keys.Count);

            try
            {
                provider.Store(master.Kek);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new VaultException(
                    $"Data keys were rewrapped but the new master key could not be stored ({ex.Message}). " +
                    $"Place it manually (provider {provider.Name}); base64 value: {kekBase64}", ex);
            }
            return new RecoverResult(true, null, keys.Count);
        }
        finally
        {
            Array.Clear(master.Kek);
        }
    }

    /// <summary>Creates a new active DEK; older DEKs stay readable. Uses the stored (KEK-wrapped) recovery key to wrap the new DEK for recovery.</summary>
    public async Task<int> RotateDekAsync(CancellationToken ct)
    {
        var meta = await db.VaultMetadata.SingleOrDefaultAsync(ct) ?? throw new VaultNotInitializedException();
        var keys = await db.DataKeys.ToListAsync(ct);
        var kek = provider.GetKek();
        byte[]? recoveryKey = null;
        try
        {
            recoveryKey = KeyWrapper.Unwrap(meta.RecoveryKeyWrappedByMaster, kek);
            var now = clock.GetUtcNow();
            var newKey = KeyHierarchy.CreateDataKey(keys.Max(k => k.Version) + 1, kek, recoveryKey, now);
            foreach (var key in keys) key.IsActive = false;
            newKey.IsActive = true;
            db.DataKeys.Add(newKey);
            db.AuditLogs.Add(new AuditLog { Timestamp = now, ClientId = VaultInitializer.AuditClientId, Action = "rotate-dek", Success = true, Detail = $"version={newKey.Version}" });
            await db.SaveChangesAsync(ct);
            return newKey.Version;
        }
        catch (CryptographicException ex)
        {
            throw new VaultException("The master key does not unwrap the stored recovery key.", ex);
        }
        finally
        {
            Array.Clear(kek);
            if (recoveryKey is not null) Array.Clear(recoveryKey);
        }
    }
}
