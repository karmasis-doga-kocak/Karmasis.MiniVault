using MiniVault.Server.Hosting;

namespace MiniVault.Server.Tests.Hosting;

public class TlsOptionsTests
{
    private static TlsOptions Valid() => new()
    {
        Url = "https://0.0.0.0:8200",
        Certificate = new TlsOptions.CertificateOptions { Thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD" },
    };

    [Fact]
    public void Validate_AcceptsDefaultUrl_WithThumbprint()
    {
        Should.NotThrow(() => Valid().Validate());
    }

    [Fact]
    public void Validate_Throws_WhenUrlIsHttp()
    {
        var tls = Valid();
        tls.Url = "http://0.0.0.0:8200";

        Should.Throw<InvalidOperationException>(() => tls.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenUrlIsNotAbsolute()
    {
        var tls = Valid();
        tls.Url = "not-a-url";

        Should.Throw<InvalidOperationException>(() => tls.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenNeitherPathNorThumbprintSet_AndDevCertNotAllowed()
    {
        var tls = Valid();
        tls.Certificate = new TlsOptions.CertificateOptions();

        Should.Throw<InvalidOperationException>(() => tls.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenBothPathAndThumbprintSet()
    {
        var tls = Valid();
        tls.Certificate.Path = @"C:\certs\minivault.pfx";

        Should.Throw<InvalidOperationException>(() => tls.Validate());
    }

    [Fact]
    public void Validate_Allows_DevCertificate_WithNoCertificateConfigured()
    {
        var tls = new TlsOptions { AllowDevelopmentCertificate = true };

        Should.NotThrow(() => tls.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenStoreLocationIsInvalid()
    {
        var tls = Valid();
        tls.Certificate.StoreLocation = "Nowhere";

        Should.Throw<InvalidOperationException>(() => tls.Validate());
    }

    [Theory]
    [InlineData("LocalMachine")]
    [InlineData("currentuser")]
    public void Validate_Accepts_KnownStoreLocations_CaseInsensitive(string location)
    {
        var tls = Valid();
        tls.Certificate.StoreLocation = location;

        Should.NotThrow(() => tls.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenStoreNameIsEmpty()
    {
        var tls = Valid();
        tls.Certificate.StoreName = "";

        Should.Throw<InvalidOperationException>(() => tls.Validate());
    }

    [Fact]
    public void Validate_ErrorMessage_ForHttpUrl_NamesTheUrl()
    {
        var tls = Valid();
        tls.Url = "http://0.0.0.0:8200";

        var ex = Should.Throw<InvalidOperationException>(() => tls.Validate());

        ex.Message.ShouldContain("http://0.0.0.0:8200");
    }
}
