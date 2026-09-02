namespace MiniVault.Server.Secrets;

public sealed class SecretConflictException : Exception
{
    public string Name { get; }

    public SecretConflictException(string name) : this(name, null) { }

    public SecretConflictException(string name, Exception? inner) : base($"Secret '{name}' was modified concurrently.", inner) => Name = name;
}
