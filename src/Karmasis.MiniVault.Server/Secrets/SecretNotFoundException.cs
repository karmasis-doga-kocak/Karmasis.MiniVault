namespace Karmasis.MiniVault.Server.Secrets;

public sealed class SecretNotFoundException(string name) : Exception($"Secret '{name}' was not found.")
{
    public string Name { get; } = name;
}
