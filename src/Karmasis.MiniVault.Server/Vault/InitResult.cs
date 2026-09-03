using Karmasis.MiniVault.Server.Keys;

namespace Karmasis.MiniVault.Server.Vault;

/// <param name="MasterKeyBase64">Set only when the provider cannot store the key; the operator must place it (e.g. MINIVAULT__MASTERKEY).</param>
public sealed record InitResult(RecoveryMaterial Recovery, bool MasterKeyStored, string? MasterKeyBase64);
