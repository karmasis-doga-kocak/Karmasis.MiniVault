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

        var master = options.MasterKeyPassword is null
            ? MasterKeyMaterial.Random()
            : MasterKeyMaterial.FromPassword(options.MasterKeyPassword);
        var recovery = RecoveryMaterial.Generate(options.RecoveryMode, options.Shares, options.Threshold);
        var now = clock.GetUtcNow();

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
        await db.SaveChangesAsync(ct);

        if (provider.CanStore)
        {
            provider.Store(master.Kek);
            return new InitResult(recovery, true, null);
        }
        return new InitResult(recovery, false, Convert.ToBase64String(master.Kek));
    }
}
