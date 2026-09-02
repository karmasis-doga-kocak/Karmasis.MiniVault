using Karmasis.Cryptography.Keys;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Tests.Keys;

public class MasterKeyMaterialTests
{
    [Fact]
    public void Random_Has32ByteKekAndNoSalt()
    {
        var m = MasterKeyMaterial.Random();

        m.Kek.Length.ShouldBe(32);
        m.Salt.ShouldBeNull();
        m.Iterations.ShouldBeNull();
    }

    [Fact]
    public void FromPassword_IsDeterministicGivenSaltAndIterations()
    {
        var first = MasterKeyMaterial.FromPassword("MasterKey!");

        first.Salt!.Length.ShouldBe(16);
        first.Iterations.ShouldBe(KeyDerivation.DefaultIterations);
        var again = MasterKeyMaterial.FromPassword("MasterKey!", first.Salt!, first.Iterations!.Value);
        again.Kek.ShouldBe(first.Kek);
        MasterKeyMaterial.FromPassword("Other!", first.Salt!, first.Iterations!.Value).Kek.ShouldNotBe(first.Kek);
    }

    [Fact]
    public void FromPassword_EmptyPassword_Throws() =>
        Should.Throw<ArgumentException>(() => MasterKeyMaterial.FromPassword(""));
}
