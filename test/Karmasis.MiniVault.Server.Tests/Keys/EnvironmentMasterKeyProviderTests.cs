using Karmasis.MiniVault.Server.Keys;

namespace Karmasis.MiniVault.Server.Tests.Keys;

[Collection("EnvironmentVariables")]
public class EnvironmentMasterKeyProviderTests : IDisposable
{
    public EnvironmentMasterKeyProviderTests() => Environment.SetEnvironmentVariable(EnvironmentMasterKeyProvider.VariableName, null);
    public void Dispose() => Environment.SetEnvironmentVariable(EnvironmentMasterKeyProvider.VariableName, null);

    [Fact]
    public void GetKek_ReturnsDecodedKey_WhenVariableHolds32Bytes()
    {
        var kek = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Environment.SetEnvironmentVariable(EnvironmentMasterKeyProvider.VariableName, Convert.ToBase64String(kek));
        var provider = new EnvironmentMasterKeyProvider();

        provider.Exists().ShouldBeTrue();
        provider.GetKek().ShouldBe(kek);
    }

    [Fact]
    public void GetKek_Throws_WhenVariableMissing()
    {
        var provider = new EnvironmentMasterKeyProvider();

        provider.Exists().ShouldBeFalse();
        Should.Throw<MasterKeyUnavailableException>(() => provider.GetKek());
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")] // 3 bytes
    public void GetKek_Throws_WhenVariableInvalid(string value)
    {
        Environment.SetEnvironmentVariable(EnvironmentMasterKeyProvider.VariableName, value);
        var provider = new EnvironmentMasterKeyProvider();

        Should.Throw<MasterKeyUnavailableException>(() => provider.GetKek());
    }

    [Fact]
    public void Store_IsNotSupported()
    {
        var provider = new EnvironmentMasterKeyProvider();

        provider.CanStore.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => provider.Store(new byte[32]));
    }
}

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public class EnvironmentVariablesCollection { }
