using Karmasis.Cryptography.Keys;
using Karmasis.MiniVault.Server.Data.Entities;

namespace Karmasis.MiniVault.Server.Keys;

/// <summary>
/// The recovery key and its operator-facing parts: one base64 string in Single mode,
/// n base64 Shamir shares (any k reconstruct) in Shamir mode. Parts are shown once at init and never stored.
/// </summary>
public sealed class RecoveryMaterial
{
    private RecoveryMaterial(RecoveryMode mode, byte[] key, IReadOnlyList<string> parts, int? shares, int? threshold)
    {
        Mode = mode; Key = key; Parts = parts; Shares = shares; Threshold = threshold;
    }

    public RecoveryMode Mode { get; }
    public byte[] Key { get; }
    public IReadOnlyList<string> Parts { get; }
    public int? Shares { get; }
    public int? Threshold { get; }

    public static RecoveryMaterial Generate(RecoveryMode mode, int shares = 0, int threshold = 0)
    {
        var key = KeyGenerator.GenerateKey(MasterKey.Size);
        switch (mode)
        {
            case RecoveryMode.Single:
                return new RecoveryMaterial(mode, key, [Convert.ToBase64String(key)], null, null);
            case RecoveryMode.Shamir:
                var parts = ShamirSecretSharing.Split(key, shares, threshold).Select(Convert.ToBase64String).ToArray();
                return new RecoveryMaterial(mode, key, parts, shares, threshold);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    public static byte[] Reconstruct(RecoveryMode mode, IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Recovery parts must not be null or empty.", nameof(parts));
        switch (mode)
        {
            case RecoveryMode.Single:
                if (parts.Count != 1) throw new ArgumentException("Single recovery mode expects exactly one recovery key.", nameof(parts));
                var key = Convert.FromBase64String(parts[0].Trim());
                if (key.Length != MasterKey.Size) throw new FormatException($"Recovery key must be {MasterKey.Size} bytes.");
                return key;
            case RecoveryMode.Shamir:
                if (parts.Count < 2) throw new ArgumentException("Shamir recovery needs at least two shares.", nameof(parts));
                var shares = parts.Select(p => Convert.FromBase64String(p.Trim())).ToArray();
                if (shares.Any(s => s.Length != MasterKey.Size + 1)) throw new FormatException($"Each share must be {MasterKey.Size + 1} bytes.");
                return ShamirSecretSharing.Combine(shares);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
