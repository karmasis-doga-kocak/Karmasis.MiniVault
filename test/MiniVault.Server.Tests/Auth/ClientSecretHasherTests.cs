using MiniVault.Server.Auth;

namespace MiniVault.Server.Tests.Auth;

public class ClientSecretHasherTests
{
    [Fact]
    public void GenerateSecret_Is32RandomBytesBase64()
    {
        var a = ClientSecretHasher.GenerateSecret();
        var b = ClientSecretHasher.GenerateSecret();
        Convert.FromBase64String(a).Length.ShouldBe(32);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Hash_ThenVerify_Succeeds_AndWrongSecretFails()
    {
        var secret = ClientSecretHasher.GenerateSecret();
        var (hash, salt, iterations) = ClientSecretHasher.Hash(secret);

        hash.Length.ShouldBe(32);
        salt.Length.ShouldBe(16);
        iterations.ShouldBe(ClientSecretHasher.DefaultIterations);
        ClientSecretHasher.Verify(secret, hash, salt, iterations).ShouldBeTrue();
        ClientSecretHasher.Verify(secret + "x", hash, salt, iterations).ShouldBeFalse();
        ClientSecretHasher.Verify(secret, hash, salt, iterations + 1).ShouldBeFalse();
    }
}
