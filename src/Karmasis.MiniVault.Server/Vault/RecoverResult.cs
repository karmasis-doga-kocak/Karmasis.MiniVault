namespace Karmasis.MiniVault.Server.Vault;

public sealed record RecoverResult(bool MasterKeyStored, string? MasterKeyBase64, int DataKeysRewrapped);
