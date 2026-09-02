using System.Security.Cryptography;
using Karmasis.Cryptography.Keys;

namespace MiniVault.Server.Auth;

public static class ClientSecretHasher
{
    public const int DefaultIterations = 100_000;
    private const int SaltSize = 16;

    public static string GenerateSecret() => Convert.ToBase64String(KeyGenerator.GenerateKey(32));

    public static (byte[] Hash, byte[] Salt, int Iterations) Hash(string secret)
    {
        var salt = KeyGenerator.GenerateKey(SaltSize);
        return (KeyDerivation.FromPassword(secret, salt, DefaultIterations), salt, DefaultIterations);
    }

    public static bool Verify(string secret, byte[] hash, byte[] salt, int iterations)
    {
        if (string.IsNullOrEmpty(secret) || hash.Length == 0 || salt.Length == 0 || iterations <= 0) return false;
        var candidate = KeyDerivation.FromPassword(secret, salt, iterations);
        try { return CryptographicOperations.FixedTimeEquals(candidate, hash); }
        finally { Array.Clear(candidate); }
    }
}
