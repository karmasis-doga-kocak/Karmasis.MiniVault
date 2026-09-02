using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace MiniVault.Server.Hosting;

/// <summary>
/// Wires Kestrel to the single HTTPS endpoint described by <see cref="TlsOptions"/>. Because the endpoint is
/// registered explicitly via <see cref="KestrelServerOptions.Listen(IPEndPoint, Action{ListenOptions})"/>, Kestrel
/// never falls back to <c>ASPNETCORE_URLS</c>/<c>--urls</c>/<c>Kestrel:Endpoints</c> configuration, and no
/// plain-HTTP listener is ever created.
/// </summary>
public static class KestrelConfiguration
{
    /// <summary>Loads the certificate named by <paramref name="cert"/> (a PFX file or a store thumbprint).
    /// Callers own the returned certificate and must dispose it.</summary>
    public static X509Certificate2 LoadCertificate(TlsOptions.CertificateOptions cert)
    {
        if (!string.IsNullOrWhiteSpace(cert.Path))
            return LoadFromFile(cert.Path, cert.Password);

        if (!string.IsNullOrWhiteSpace(cert.Thumbprint))
            return LoadFromStore(cert.Thumbprint, cert.StoreName, cert.StoreLocation);

        throw new InvalidOperationException("Tls:Certificate must set Path or Thumbprint.");
    }

    private static X509Certificate2 LoadFromFile(string path, string? password)
    {
        try
        {
            // MachineKeySet puts the private key in the machine-wide key store instead of the current user's
            // profile, so a service account with no loaded profile (e.g. LocalSystem, or a Windows Service
            // running non-interactively) can still use the certificate. MachineKeySet has no meaning outside
            // Windows, so other platforms keep DefaultKeySet.
            var keyStorageFlags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.DefaultKeySet;
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, keyStorageFlags);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Never include the password in the exception message.
            throw new InvalidOperationException(
                $"Could not load the TLS certificate from '{path}'. Check that the file exists and that Tls:Certificate:Password is correct.", ex);
        }
    }

    private static X509Certificate2 LoadFromStore(string thumbprint, string storeName, string storeLocation)
    {
        var location = Enum.Parse<StoreLocation>(storeLocation, ignoreCase: true);
        var normalized = NormalizeThumbprint(thumbprint);

        using var store = new X509Store(storeName, location);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);

        X509Certificate2? found = null;
        foreach (var candidate in matches)
        {
            if (found is null && candidate.HasPrivateKey)
                found = new X509Certificate2(candidate);
            candidate.Dispose();
        }

        return found ?? throw new InvalidOperationException(
            $"No certificate with thumbprint '{normalized}' and a private key was found in {storeLocation}\\{storeName}.");
    }

    /// <summary>Strips everything but ASCII hex digits (dropping separators such as ':' or '-', whitespace, and
    /// invisible characters like U+200E LEFT-TO-RIGHT MARK that can be pasted in from some certificate tools) and
    /// upper-cases the result.</summary>
    internal static string NormalizeThumbprint(string thumbprint)
    {
        var chars = new char[thumbprint.Length];
        var count = 0;
        foreach (var c in thumbprint)
        {
            if (Uri.IsHexDigit(c))
                chars[count++] = char.ToUpperInvariant(c);
        }
        return new string(chars, 0, count);
    }

    /// <summary>Message used by both <see cref="Apply"/> and <see cref="TlsStartupCheck"/> when
    /// <see cref="TlsOptions.AllowDevelopmentCertificate"/> is used outside Development without
    /// <see cref="TlsOptions.AllowDevelopmentCertificateOutsideDevelopment"/>.</summary>
    internal const string DevelopmentCertificateNotAllowedMessage =
        "Tls:AllowDevelopmentCertificate is only allowed in the Development environment. Configure Tls:Certificate:Path or Tls:Certificate:Thumbprint.";

    /// <summary>Configures Kestrel to listen on <see cref="TlsOptions.Url"/> only, over HTTPS.</summary>
    public static void Apply(WebApplicationBuilder builder, TlsOptions tls)
    {
        if (tls.AllowDevelopmentCertificate && !builder.Environment.IsDevelopment() && !tls.AllowDevelopmentCertificateOutsideDevelopment)
            throw new InvalidOperationException(DevelopmentCertificateNotAllowedMessage);

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            loggerFactory.CreateLogger(nameof(KestrelConfiguration)).LogWarning(
                "ASPNETCORE_URLS is set but is ignored: MiniVault listens only on Tls:Url ({TlsUrl}) because HTTP is never enabled.", tls.Url);
        }

        var endpoint = ParseEndpoint(tls.Url);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(endpoint, listen =>
            {
                if (tls.AllowDevelopmentCertificate)
                    listen.UseHttps();
                else
                    listen.UseHttps(LoadCertificate(tls.Certificate));
            });
        });
    }

    /// <summary>Resolves the host part of <paramref name="url"/> to an <see cref="IPEndPoint"/>. Hostnames are not
    /// supported — Kestrel must bind to a concrete address, so the URL host must be an IP literal (or the
    /// well-known "0.0.0.0" / "localhost" / "::" shorthands).</summary>
    internal static IPEndPoint ParseEndpoint(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var address = uri.Host switch
        {
            "0.0.0.0" => IPAddress.Any,
            "localhost" => IPAddress.Loopback,
            "::" => IPAddress.IPv6Any,
            var host => IPAddress.TryParse(host, out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"Tls:Url host '{host}' is not an IP address. Bind to an IP (e.g. 0.0.0.0, ::, or a specific address) — hostnames are not supported."),
        };
        return new IPEndPoint(address, uri.Port);
    }
}
