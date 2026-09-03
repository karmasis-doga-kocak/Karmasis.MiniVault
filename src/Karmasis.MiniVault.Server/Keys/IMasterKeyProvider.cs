namespace Karmasis.MiniVault.Server.Keys;

/// <summary>
/// Supplies the raw 32-byte key encryption key (KEK, the "MasterKey").
/// The KEK never touches the database; it only wraps data encryption keys.
/// </summary>
public interface IMasterKeyProvider
{
    string Name { get; }
    /// <summary>True when <see cref="Store"/> persists the key (DPAPI file). False when the operator must place it (environment variable).</summary>
    bool CanStore { get; }
    bool Exists();
    /// <summary>Returns a copy of the KEK. Throws <see cref="MasterKeyUnavailableException"/> when it cannot be read.</summary>
    byte[] GetKek();
    /// <summary>Persists the KEK. Throws <see cref="NotSupportedException"/> when <see cref="CanStore"/> is false.</summary>
    void Store(byte[] kek);
}

public static class MasterKey
{
    public const int Size = 32;

    public static void ValidateSize(byte[] kek, string paramName)
    {
        ArgumentNullException.ThrowIfNull(kek, paramName);
        if (kek.Length != Size) throw new ArgumentException($"Master key must be {Size} bytes.", paramName);
    }
}
