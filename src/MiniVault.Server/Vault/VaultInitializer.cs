using Karmasis.Cryptography.Keys;
using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Vault;

public sealed class VaultInitializer(MiniVaultDbContext db, IMasterKeyProvider provider, TimeProvider clock)
{
    public const string AuditClientId = "cli";

    public async Task<InitResult> InitializeAsync(InitOptions options, CancellationToken ct)
    {
        await db.Database.MigrateAsync(ct);
        if (await db.VaultMetadata.AnyAsync(ct))
            throw new VaultAlreadyInitializedException();
        if (provider.CanStore && provider.Exists() && !options.Force)
            throw new VaultException($"A master key already exists in the {provider.Name} provider. Another vault on this host would lose its key. Pass --force to overwrite it.");

        var master = options.MasterKeyPassword is null
            ? MasterKeyMaterial.Random()
            : MasterKeyMaterial.FromPassword(options.MasterKeyPassword);
        var recovery = RecoveryMaterial.Generate(options.RecoveryMode, options.Shares, options.Threshold);
        var now = clock.GetUtcNow();

        try
        {
            var dataKey = KeyHierarchy.CreateDataKey(1, master.Kek, recovery.Key, now);
            dataKey.IsActive = true;

            db.VaultMetadata.Add(new VaultMetadata
            {
                Id = VaultMetadata.SingletonId,
                RecoveryMode = recovery.Mode,
                Shares = recovery.Shares,
                Threshold = recovery.Threshold,
                KekSalt = master.Salt,
                KekIterations = master.Iterations,
                RecoveryKeyWrappedByMaster = KeyWrapper.Wrap(recovery.Key, master.Kek),
                InitializedAt = now,
            });
            db.DataKeys.Add(dataKey);
            db.AuditLogs.Add(new AuditLog { Timestamp = now, ClientId = AuditClientId, Action = "init", Success = true, Detail = $"recovery={recovery.Mode}" });

            // Persist the KEK before the database row: a failed Store must not leave an initialized vault without a key.
            // A key file left behind by a failed SaveChanges is harmless; the next init overwrites it.
            string? kekBase64 = null;
            if (provider.CanStore) provider.Store(master.Kek);
            else kekBase64 = Convert.ToBase64String(master.Kek);

            await db.SaveChangesAsync(ct);
            return new InitResult(recovery, provider.CanStore, kekBase64);
        }
        finally
        {
            Array.Clear(master.Kek);
        }
    }
}
