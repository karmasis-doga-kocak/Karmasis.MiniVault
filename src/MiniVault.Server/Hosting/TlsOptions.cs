namespace MiniVault.Server.Hosting;

/// <summary>
/// Kestrel binds HTTPS only; there is no plain-HTTP listener. <see cref="Validate"/> is called by
/// <see cref="TlsStartupCheck"/> so a misconfigured install fails fast instead of silently refusing connections.
/// </summary>
public sealed class TlsOptions
{
    public const string SectionName = "Tls";

    /// <summary>The single endpoint Kestrel listens on. Must be an absolute https:// URL with an IP host
    /// (0.0.0.0, ::, or a specific address) — hostnames are not resolved. Default: https://0.0.0.0:8200.</summary>
    public string Url { get; set; } = "https://0.0.0.0:8200";

    public CertificateOptions Certificate { get; set; } = new();

    /// <summary>Development only: use Kestrel's ASP.NET Core HTTPS development certificate
    /// (<c>dotnet dev-certs https --trust</c>) instead of a configured certificate.</summary>
    public bool AllowDevelopmentCertificate { get; set; }

    /// <summary>Throws <see cref="InvalidOperationException"/> with a message naming the offending setting;
    /// never includes <see cref="CertificateOptions.Password"/>.</summary>
    public void Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Tls:Url '{Url}' must be an absolute https:// URL, e.g. 'https://0.0.0.0:8200'.");

        if (!AllowDevelopmentCertificate)
        {
            var hasPath = !string.IsNullOrWhiteSpace(Certificate.Path);
            var hasThumbprint = !string.IsNullOrWhiteSpace(Certificate.Thumbprint);
            if (hasPath == hasThumbprint)
                throw new InvalidOperationException(
                    "Tls:Certificate must set exactly one of Path or Thumbprint, or set Tls:AllowDevelopmentCertificate=true (Development only).");
        }

        if (!string.Equals(Certificate.StoreLocation, "LocalMachine", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Certificate.StoreLocation, "CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Tls:Certificate:StoreLocation '{Certificate.StoreLocation}' must be 'LocalMachine' or 'CurrentUser'.");

        if (string.IsNullOrWhiteSpace(Certificate.StoreName))
            throw new InvalidOperationException("Tls:Certificate:StoreName must not be empty.");
    }

    public sealed class CertificateOptions
    {
        /// <summary>Path to a PFX/PKCS#12 file. Mutually exclusive with <see cref="Thumbprint"/>.</summary>
        public string? Path { get; set; }

        /// <summary>Password for the PFX at <see cref="Path"/>. Never logged.</summary>
        public string? Password { get; set; }

        /// <summary>Thumbprint of a certificate (with a private key) already installed in the configured
        /// certificate store. Mutually exclusive with <see cref="Path"/>.</summary>
        public string? Thumbprint { get; set; }

        public string StoreName { get; set; } = "My";

        /// <summary>"LocalMachine" or "CurrentUser" (case-insensitive).</summary>
        public string StoreLocation { get; set; } = "LocalMachine";
    }
}
