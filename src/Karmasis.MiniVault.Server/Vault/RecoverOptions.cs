namespace Karmasis.MiniVault.Server.Vault;

/// <param name="RecoveryParts">One recovery key (Single) or at least threshold shares (Shamir), base64.</param>
/// <param name="NewMasterKeyPassword">Null: generate a random new KEK.</param>
public sealed record RecoverOptions(IReadOnlyList<string> RecoveryParts, string? NewMasterKeyPassword);
