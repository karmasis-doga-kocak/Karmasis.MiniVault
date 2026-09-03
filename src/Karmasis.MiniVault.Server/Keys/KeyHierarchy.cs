using Karmasis.Cryptography.Keys;
using Karmasis.MiniVault.Server.Data.Entities;

namespace Karmasis.MiniVault.Server.Keys;

/// <summary>
/// DEK/KEK hierarchy: every data encryption key (DEK) is wrapped twice, by the master key (KEK)
/// for daily use and by the recovery key for master-key loss. Data is encrypted with DEKs only.
/// </summary>
public static class KeyHierarchy
{
    public static DataKey CreateDataKey(int version, byte[] kek, byte[] recoveryKey, DateTimeOffset now)
    {
        MasterKey.ValidateSize(kek, nameof(kek));
        MasterKey.ValidateSize(recoveryKey, nameof(recoveryKey));
        var dek = KeyGenerator.GenerateKey();
        try
        {
            return new DataKey
            {
                Version = version,
                WrappedByMaster = KeyWrapper.Wrap(dek, kek),
                WrappedByRecovery = KeyWrapper.Wrap(dek, recoveryKey),
                IsActive = false,
                CreatedAt = now,
            };
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    public static byte[] UnwrapWithMaster(DataKey key, byte[] kek) => KeyWrapper.Unwrap(key.WrappedByMaster, kek);

    public static byte[] UnwrapWithRecovery(DataKey key, byte[] recoveryKey) => KeyWrapper.Unwrap(key.WrappedByRecovery, recoveryKey);

    public static void RewrapWithMaster(DataKey key, byte[] dek, byte[] newKek)
    {
        MasterKey.ValidateSize(newKek, nameof(newKek));
        key.WrappedByMaster = KeyWrapper.Wrap(dek, newKek);
    }
}
