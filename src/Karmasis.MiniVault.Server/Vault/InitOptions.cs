using Karmasis.MiniVault.Server.Data.Entities;

namespace Karmasis.MiniVault.Server.Vault;

public sealed record InitOptions(RecoveryMode RecoveryMode, int Shares = 0, int Threshold = 0, string? MasterKeyPassword = null, bool Force = false);
