using System.Text;
using Karmasis.Cryptography.Keys;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Secrets;

/// <summary>Encrypts secret values with the active DEK. The secret name is bound as associated data so a ciphertext cannot be moved between names.</summary>
public sealed class SecretCipher(DataKeyRing ring)
{
    public (byte[] Ciphertext, int DekVersion) Encrypt(string name, byte[] value)
    {
        var (version, dek) = ring.GetActive();
        try { return (AeadCipher.Encrypt(value, dek, Encoding.UTF8.GetBytes(name)), version); }
        finally { Array.Clear(dek); }
    }

    public async Task<byte[]> DecryptAsync(string name, byte[] ciphertext, int dekVersion, CancellationToken ct)
    {
        var dek = await ring.GetDekAsync(dekVersion, ct);
        try { return AeadCipher.Decrypt(ciphertext, dek, Encoding.UTF8.GetBytes(name)); }
        finally { Array.Clear(dek); }
    }
}
