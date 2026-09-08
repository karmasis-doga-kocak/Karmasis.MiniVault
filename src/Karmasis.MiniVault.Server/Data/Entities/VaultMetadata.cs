namespace Karmasis.MiniVault.Server.Data.Entities;

/// <summary>Single row (Id = 1) written by 'minivault init'.</summary>
public sealed class VaultMetadata
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public RecoveryMode RecoveryMode { get; set; }
    public int? Shares { get; set; }
    public int? Threshold { get; set; }
    /// <summary>Salt used when the KEK was derived from a password; null when the KEK was generated randomly.</summary>
    public byte[]? KekSalt { get; set; }
    /// <summary>PBKDF2 iteration count that goes with <see cref="KekSalt"/>.</summary>
    public int? KekIterations { get; set; }
    /// <summary>Recovery key wrapped by the current KEK, so rotate-dek can wrap new DEKs for recovery without operator input.</summary>
    public byte[] RecoveryKeyWrappedByMaster { get; set; } = [];
    public DateTimeOffset InitializedAt { get; set; }
}
