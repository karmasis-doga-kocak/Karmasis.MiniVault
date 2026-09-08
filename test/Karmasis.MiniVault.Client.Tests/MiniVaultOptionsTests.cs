using Karmasis.MiniVault.Client;

namespace Karmasis.MiniVault.Client.Tests;

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

    [Fact]
    public void Validate_Throws_WhenThumbprintNormalizesToEmpty()
    {
        var options = Valid();
        options.ServerCertificateThumbprint = "::";
        Should.Throw<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenThumbprintIs39HexChars()
    {
        var options = Valid();
        options.ServerCertificateThumbprint = new string('A', 39);
        Should.Throw<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Passes_WhenThumbprintIs40HexChars_WithColons()
    {
        var options = Valid();
        options.ServerCertificateThumbprint = string.Join(":", Enumerable.Repeat("AB", 20));
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Passes_WhenThumbprintIsNull()
    {
        var options = Valid();
        options.ServerCertificateThumbprint = null;
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenRefreshIntervalIsZero()
    {
        var options = Valid();
        options.RefreshInterval = TimeSpan.Zero;
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("RefreshInterval");
    }

    [Fact]
    public void Validate_Throws_WhenRefreshIntervalIsNegative()
    {
        var options = Valid();
        options.RefreshInterval = TimeSpan.FromSeconds(-1);
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("RefreshInterval");
    }

    [Fact]
    public void Validate_Throws_WhenRefreshIntervalIsUnderOneSecond()
    {
        var options = Valid();
        options.RefreshInterval = TimeSpan.FromMilliseconds(999);
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("RefreshInterval");
    }

    [Fact]
    public void Validate_Passes_WhenRefreshIntervalIsExactlyOneSecond()
    {
        var options = Valid();
        options.RefreshInterval = TimeSpan.FromSeconds(1);
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Passes_WhenRefreshIntervalIsNull()
    {
        var options = Valid();
        options.RefreshInterval = null;
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenMaxCacheAgeIsZero()
    {
        var options = Valid();
        options.MaxCacheAge = TimeSpan.Zero;
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("MaxCacheAge");
    }

    [Fact]
    public void Validate_Throws_WhenMaxCacheAgeIsNegative()
    {
        var options = Valid();
        options.MaxCacheAge = TimeSpan.FromSeconds(-1);
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("MaxCacheAge");
    }

    [Fact]
    public void Validate_Throws_WhenTimeoutIsUnderOneSecond()
    {
        var options = Valid();
        options.Timeout = TimeSpan.FromMilliseconds(500);
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("Timeout");
    }

    [Fact]
    public void Validate_Throws_WhenTimeoutIsLongerThanADay()
    {
        var options = Valid();
        options.Timeout = TimeSpan.FromDays(2);
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("Timeout");
    }

    [Fact]
    public void Validate_Passes_WhenTimeoutIsExactlyOneDay()
    {
        var options = Valid();
        options.Timeout = TimeSpan.FromDays(1);
        Should.NotThrow(() => options.Validate());
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a:b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("has space")]
    public void Validate_Throws_WhenClientIdHasCharactersTheServerRejects(string clientId)
    {
        var options = Valid();
        options.ClientId = clientId;
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("ClientId");
    }

    [Fact]
    public void Validate_Throws_WhenClientIdIsLongerThan128Characters()
    {
        var options = Valid();
        options.ClientId = new string('a', 129);
        Should.Throw<ArgumentException>(() => options.Validate()).ParamName.ShouldBe("ClientId");
    }

    [Theory]
    [InlineData("dataskope-collector")]
    [InlineData("Client.Id_1-2")]
    public void Validate_Passes_ForClientIdsTheServerAccepts(string clientId)
    {
        var options = Valid();
        options.ClientId = clientId;
        Should.NotThrow(() => options.Validate());
    }
}
