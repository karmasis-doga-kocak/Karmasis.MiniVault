namespace MiniVault.Server.Secrets;

public sealed class SecretConflictException : Exception
{
    public string Name { get; }

    public SecretConflictException(string name) : this(name, (Exception?)null) { }

    public SecretConflictException(string name, Exception? inner) : base($"Secret '{name}' was modified concurrently.", inner) => Name = name;

    private SecretConflictException(string name, string message) : base(message) => Name = name;

    /// <summary>A row whose name differs only by letter case already occupies this key under a case-insensitive collation.</summary>
    public static SecretConflictException CaseVariant(string name) =>
        new(name, $"A secret whose name differs from '{name}' only by letter case already exists; secret names are case-sensitive.");
}
