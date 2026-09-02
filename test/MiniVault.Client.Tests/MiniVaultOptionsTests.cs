using MiniVault.Client;

namespace MiniVault.Client.Tests;

public class MiniVaultOptionsTests
{
    private static MiniVaultOptions Valid() => new MiniVaultOptions
    {
        BaseUrl = "https://vault.test",
        ClientId = "client",
        ClientSecret = "secret",
    };

    [Fact]
    public void Validate_Throws_WhenBaseUrlMissing()
    {
        var options = Valid();
        options.BaseUrl = "";
        Should.Throw<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenClientIdMissing()
    {
        var options = Valid();
        options.ClientId = "";
        Should.Throw<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenClientSecretMissing()
    {
        var options = Valid();
        options.ClientSecret = "";
        Should.Throw<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenBaseUrlIsNotAWellFormedAbsoluteUrl()
    {
        var options = Valid();
        options.BaseUrl = "not a url";
        Should.Throw<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenBaseUrlIsNotHttps_AndInsecureNotAllowed()
    {
        var options = Valid();
        options.BaseUrl = "http://vault.test";
        Should.Throw<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Passes_WhenBaseUrlIsHttps()
    {
        var options = Valid();
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Passes_WhenBaseUrlIsHttp_AndInsecureAllowed()
    {
        var options = Valid();
        options.BaseUrl = "http://vault.test";
        options.AllowInsecureHttp = true;
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenTimeoutIsZero()
    {
        var options = Valid();
        options.Timeout = TimeSpan.Zero;
        Should.Throw<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenTimeoutIsNegative()
    {
        var options = Valid();
        options.Timeout = TimeSpan.FromSeconds(-1);
        Should.Throw<ArgumentException>(() => options.Validate());
    }
}
