using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MiniVault.Server.Hosting;

namespace MiniVault.Server.Tests.Hosting;

public class KestrelConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "minivault-tls-tests", Guid.NewGuid().ToString("N"));

    public KestrelConfigurationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static byte[] CreateSelfSignedPfx(string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=minivault-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return cert.Export(X509ContentType.Pfx, password);
    }

    [Fact]
    public void LoadCertificate_FromPfxFile_ReturnsCertificateWithPrivateKey()
    {
        var path = Path.Combine(_dir, "cert.pfx");
        File.WriteAllBytes(path, CreateSelfSignedPfx("pw"));

        using var loaded = KestrelConfiguration.LoadCertificate(new TlsOptions.CertificateOptions { Path = path, Password = "pw" });

        loaded.HasPrivateKey.ShouldBeTrue();
        loaded.Subject.ShouldBe("CN=minivault-test");
    }

    [Fact]
    public void LoadCertificate_WrongPassword_ThrowsInvalidOperationException_WithPathButNotPassword()
    {
        var path = Path.Combine(_dir, "cert.pfx");
        File.WriteAllBytes(path, CreateSelfSignedPfx("correct-password"));

        var ex = Should.Throw<InvalidOperationException>(() =>
            KestrelConfiguration.LoadCertificate(new TlsOptions.CertificateOptions { Path = path, Password = "wrong-password" }));

        ex.Message.ShouldContain(path);
        ex.Message.ShouldNotContain("correct-password");
        ex.Message.ShouldNotContain("wrong-password");
    }

    [Fact]
    public void LoadCertificate_MissingFile_ThrowsInvalidOperationException()
    {
        var path = Path.Combine(_dir, "missing.pfx");

        var ex = Should.Throw<InvalidOperationException>(() =>
            KestrelConfiguration.LoadCertificate(new TlsOptions.CertificateOptions { Path = path, Password = "pw" }));

        ex.Message.ShouldContain(path);
    }

    [Fact]
    public void LoadCertificate_ByThumbprint_FindsInstalledCertificate_AndDisposesInFinally()
    {
        if (!OperatingSystem.IsWindows()) return; // certificate stores are a Windows concept in this test's shape

        using var generated = X509CertificateLoader.LoadPkcs12(CreateSelfSignedPfx("pw"), "pw", X509KeyStorageFlags.Exportable);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(generated);
        try
        {
            var thumbprintWithColonsAndLowercase = string.Join(":", Chunk(generated.Thumbprint!)).ToLowerInvariant();

            using var loaded = KestrelConfiguration.LoadCertificate(new TlsOptions.CertificateOptions
            {
                Thumbprint = thumbprintWithColonsAndLowercase,
                StoreName = "My",
                StoreLocation = "CurrentUser",
            });

            loaded.HasPrivateKey.ShouldBeTrue();
            loaded.Thumbprint.ShouldBe(generated.Thumbprint);
        }
        finally
        {
            store.Remove(generated);
        }
    }

    [Fact]
    public void LoadCertificate_UnknownThumbprint_ThrowsInvalidOperationException_NamingStore()
    {
        if (!OperatingSystem.IsWindows()) return;

        var ex = Should.Throw<InvalidOperationException>(() => KestrelConfiguration.LoadCertificate(new TlsOptions.CertificateOptions
        {
            Thumbprint = "0000000000000000000000000000000000000A",
            StoreName = "My",
            StoreLocation = "CurrentUser",
        }));

        ex.Message.ShouldContain("CurrentUser");
        ex.Message.ShouldContain("My");
    }

    [Theory]
    [InlineData("https://0.0.0.0:8200", "0.0.0.0", 8200)]
    [InlineData("https://localhost:8443", "127.0.0.1", 8443)]
    [InlineData("https://[::]:8200", "::", 8200)]
    [InlineData("https://10.1.2.3:8200", "10.1.2.3", 8200)]
    public void ParseEndpoint_ResolvesKnownHosts(string url, string expectedAddress, int expectedPort)
    {
        var endpoint = KestrelConfiguration.ParseEndpoint(url);

        endpoint.Address.ShouldBe(IPAddress.Parse(expectedAddress));
        endpoint.Port.ShouldBe(expectedPort);
    }

    [Fact]
    public void ParseEndpoint_Throws_ForHostname()
    {
        Should.Throw<InvalidOperationException>(() => KestrelConfiguration.ParseEndpoint("https://vault.example.com:8200"));
    }

    private static IEnumerable<string> Chunk(string thumbprint)
    {
        for (var i = 0; i < thumbprint.Length; i += 2)
            yield return thumbprint.Substring(i, 2);
    }
}
