using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Karmasis.MiniVault.Server.Hosting;

namespace Karmasis.MiniVault.Server.Tests.Hosting;

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

    [Fact]
    public void NormalizeThumbprint_DropsSeparatorsAndInvisibleCharacters_AndUpperCases()
    {
        KestrelConfiguration.NormalizeThumbprint("‎ab:cd-ef").ShouldBe("ABCDEF");
    }

    private static IEnumerable<string> Chunk(string thumbprint)
    {
        for (var i = 0; i < thumbprint.Length; i += 2)
            yield return thumbprint.Substring(i, 2);
    }

    // ---- What Kestrel actually binds -------------------------------------------------------------------------
    // TestServer/WebApplicationFactory never start Kestrel, so the two tests below build a real WebApplication on
    // a random loopback port. That is the only way to prove that endpoint configuration is refused and that the
    // server ends up with exactly one HTTPS listener.

    private WebApplicationBuilder CreateRealBuilder(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        return builder;
    }

    private static WebApplication BuildApp(WebApplicationBuilder builder)
    {
        var tls = builder.Configuration.GetSection(TlsOptions.SectionName).Get<TlsOptions>() ?? new TlsOptions();
        KestrelConfiguration.Apply(builder, tls);
        return builder.Build();
    }

    /// <summary>Kestrel materializes its options while the host is built, so the refusal surfaces from Build or
    /// from StartAsync depending on the host shape; either way the server never reaches a listening state.</summary>
    [Fact]
    public async Task Apply_WithKestrelEndpointsInConfiguration_RefusesToStart()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var app = BuildApp(CreateRealBuilder(
                ("Tls:Url", "https://127.0.0.1:0"),
                ("Tls:AllowDevelopmentCertificate", "true"),
                ("Kestrel:Endpoints:Http:Url", "http://127.0.0.1:0")));
            await app.StartAsync();
        });

        ex.GetBaseException().Message.ShouldBe("Kestrel:Endpoints is not supported: MiniVault listens only on Tls:Url over HTTPS.");
    }

    [Fact]
    public async Task Apply_WithoutKestrelEndpoints_BindsExactlyOneHttpsAddress()
    {
        await using var app = await StartRealHttpsServerAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;

            addresses.Count.ShouldBe(1);
            addresses.Single().ShouldStartWith("https://");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>"preferHostingUrls"/"urls" are the configuration-key equivalents of
    /// ASPNETCORE_PREFERHOSTINGURLS/ASPNETCORE_URLS that container images and hosting panels set unconditionally.
    /// <see cref="KestrelConfiguration.Apply"/> forces PreferHostingUrls(false), so even with both present the
    /// server must still end up bound to exactly the one HTTPS address from Tls:Url, not the plain-HTTP one.</summary>
    [Fact]
    public async Task Apply_WithPreferHostingUrlsAndUrlsConfigured_StillBindsExactlyOneHttpsAddress()
    {
        await using var app = BuildApp(CreateRealBuilder(
            ("Tls:Url", "https://127.0.0.1:0"),
            ("Tls:AllowDevelopmentCertificate", "true"),
            ("preferHostingUrls", "true"),
            ("urls", "http://127.0.0.1:0")));
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;

            addresses.Count.ShouldBe(1);
            addresses.Single().ShouldStartWith("https://");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>Starts a real server on the ASP.NET Core development certificate; if this machine has none Kestrel
    /// can use, retries with a self-signed PFX written to the test's temp directory.</summary>
    private async Task<WebApplication> StartRealHttpsServerAsync()
    {
        var withDevCertificate = BuildApp(CreateRealBuilder(
            ("Tls:Url", "https://127.0.0.1:0"),
            ("Tls:AllowDevelopmentCertificate", "true")));
        try
        {
            await withDevCertificate.StartAsync();
            return withDevCertificate;
        }
        catch (Exception)
        {
            await withDevCertificate.DisposeAsync();
        }

        var pfx = Path.Combine(_dir, "kestrel.pfx");
        File.WriteAllBytes(pfx, CreateSelfSignedPfx("pw"));
        // AllowDevelopmentCertificate is explicitly turned off: appsettings.Development.json (copied next to the
        // test binaries by the project reference) turns it on, which would send us back to the dev certificate.
        var withOwnCertificate = BuildApp(CreateRealBuilder(
            ("Tls:Url", "https://127.0.0.1:0"),
            ("Tls:AllowDevelopmentCertificate", "false"),
            ("Tls:Certificate:Path", pfx),
            ("Tls:Certificate:Password", "pw")));
        await withOwnCertificate.StartAsync();
        return withOwnCertificate;
    }
}
