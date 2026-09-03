using Karmasis.Cryptography.Keys;

namespace Karmasis.MiniVault.Server.Keys;

/// <summary>A KEK plus, when derived from a password, the salt and iteration count needed to derive it again.</summary>
public sealed class MasterKeyMaterial(byte[] kek, byte[]? salt, int? iterations)
{
    public const int SaltSize = 16;

    public byte[] Kek { get; } = kek;
    public byte[]? Salt { get; } = salt;
    public int? Iterations { get; } = iterations;

    public static MasterKeyMaterial Random() => new(KeyGenerator.GenerateKey(MasterKey.Size), null, null);

    public static MasterKeyMaterial FromPassword(string password) =>
        FromPassword(password, KeyGenerator.GenerateKey(SaltSize), KeyDerivation.DefaultIterations);

    public static MasterKeyMaterial FromPassword(string password, byte[] salt, int iterations)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Master key password must not be empty.", nameof(password));
        return new MasterKeyMaterial(KeyDerivation.FromPassword(password, salt, iterations), salt, iterations);
    }
}
