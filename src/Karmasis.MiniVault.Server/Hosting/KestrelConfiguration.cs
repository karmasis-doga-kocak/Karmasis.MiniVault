using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

namespace Karmasis.MiniVault.Server.Hosting;

/// <summary>
/// Wires Kestrel to the single HTTPS endpoint described by <see cref="TlsOptions"/>, and makes sure nothing else
/// can add a second listener.
/// <para><b>Blocked (startup fails):</b> <c>Kestrel:Endpoints</c> and <c>Kestrel:EndpointDefaults</c>. An operator
/// who writes them expects them to take effect — most dangerously an extra plain-HTTP listener — so they are
/// rejected outright instead of being silently ignored.</para>
/// <para><b>Ignored (startup continues):</b> everything else under the <c>Kestrel</c> configuration section
/// (<c>Kestrel:Certificates</c>, limits, ...), because the configuration loader ASP.NET Core installs over that
/// section is replaced with an empty configuration here; and <c>ASPNETCORE_URLS</c>/<c>--urls</c>/
/// <c>ASPNETCORE_HTTP_PORTS</c>, which container images and hosting panels set unconditionally — a warning is
/// logged for <c>ASPNETCORE_URLS</c> and the explicit <c>Listen</c> below wins. <c>ASPNETCORE_PREFERHOSTINGURLS</c>
/// is pinned to <c>false</c> for the same reason: preferring hosting URLs is what would let
/// <c>ASPNETCORE_URLS</c>/<c>--urls</c> win over the explicit <c>Listen</c> call configured here instead of
/// merely being logged and ignored.</para>
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

    /// <summary>Message used by both <see cref="Apply"/> and <see cref="TlsStartupCheck"/> when a development
    /// certificate is used outside Development because
    /// <see cref="TlsOptions.AllowDevelopmentCertificateOutsideDevelopment"/> is set.</summary>
    internal const string DevelopmentCertificateOutsideDevelopmentMessage =
        "Development certificate allowed outside Development by configuration; do not use in production.";

    /// <summary>Message thrown when Kestrel endpoint configuration is present. Endpoint configuration is rejected
    /// rather than ignored so an operator is never left believing an extra listener was created.</summary>
    internal const string KestrelEndpointsNotSupportedMessage =
        "Kestrel:Endpoints is not supported: MiniVault listens only on Tls:Url over HTTPS.";

    /// <summary>True when configuration carries Kestrel endpoint definitions (<c>Kestrel:Endpoints:*</c> or
    /// <c>Kestrel:EndpointDefaults:*</c>), which MiniVault refuses to start with.</summary>
    internal static bool HasEndpointConfiguration(IConfiguration configuration) =>
        configuration.GetSection("Kestrel:Endpoints").GetChildren().Any() ||
        configuration.GetSection("Kestrel:EndpointDefaults").GetChildren().Any();

    /// <summary>Configures Kestrel to listen on <see cref="TlsOptions.Url"/> only, over HTTPS.</summary>
    public static void Apply(WebApplicationBuilder builder, TlsOptions tls)
    {
        if (tls.AllowDevelopmentCertificate && !builder.Environment.IsDevelopment() && !tls.AllowDevelopmentCertificateOutsideDevelopment)
            throw new InvalidOperationException(DevelopmentCertificateNotAllowedMessage);

        if (tls.AllowDevelopmentCertificate && !builder.Environment.IsDevelopment())
            LogBootstrap(log => log.LogCritical("{Message}", DevelopmentCertificateOutsideDevelopmentMessage));

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            LogBootstrap(log => log.LogWarning(
                "ASPNETCORE_URLS is set but is ignored: MiniVault listens only on Tls:Url ({TlsUrl}) because HTTP is never enabled.", tls.Url));
        }

        var endpoint = ParseEndpoint(tls.Url);
        var configuration = builder.Configuration;

        // Belt and braces alongside the ASPNETCORE_URLS warning above: PreferHostingUrls(true) is what would let
        // ASPNETCORE_URLS/--urls override the explicit Listen call below instead of merely being logged and
        // ignored. Force it off regardless of what ASPNETCORE_PREFERHOSTINGURLS or configuration says.
        builder.WebHost.PreferHostingUrls(false);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (HasEndpointConfiguration(configuration))
                throw new InvalidOperationException(KestrelEndpointsNotSupportedMessage);

            // ConfigureWebDefaults binds Kestrel to the "Kestrel" configuration section. Replace that loader with an
            // empty configuration before Listen, so nothing from configuration can add a listener behind our back.
            kestrel.Configure(new ConfigurationBuilder().Build());

            kestrel.Listen(endpoint, listen =>
            {
                if (tls.AllowDevelopmentCertificate)
                    listen.UseHttps();
                else
                    listen.UseHttps(LoadCertificate(tls.Certificate));
            });
        });
    }

    /// <summary>Logs a single line before the host (and its logging pipeline) exists.</summary>
    private static void LogBootstrap(Action<ILogger> log)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        log(loggerFactory.CreateLogger(nameof(KestrelConfiguration)));
    }

    /// <summary>Belt-and-braces check run once Kestrel has actually bound its sockets: everything in
    /// <see cref="Apply"/> (the endpoint-configuration guard, pinning <c>PreferHostingUrls</c> to false, ignoring
    /// <c>ASPNETCORE_URLS</c>) exists to make this true, but this is the check that fails loudly if some future
    /// hosting change — or a hosting layer MiniVault does not control — lets a second listener or a non-HTTPS
    /// address through anyway.</summary>
    /// <exception cref="InvalidOperationException">The server is not bound to exactly one address, or that address
    /// does not start with <c>https://</c>.</exception>
    public static void AssertSingleHttpsAddress(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? Array.Empty<string>();
        var httpsAddresses = addresses.Where(a => a.StartsWith("https://", StringComparison.Ordinal)).ToList();

        if (addresses.Count != 1 || httpsAddresses.Count != 1)
        {
            throw new InvalidOperationException(
                $"MiniVault bound an unexpected address: expected exactly one https:// address, got [{string.Join(", ", addresses)}].");
        }
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
