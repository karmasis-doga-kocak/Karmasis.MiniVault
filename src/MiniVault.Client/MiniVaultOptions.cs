using System;

namespace MiniVault.Client;

/// <summary>
/// Configuration for an <see cref="IMiniVaultClient"/>: server location, credentials, local caching, and
/// networking behavior.
/// </summary>
public sealed class MiniVaultOptions
{
    /// <summary>The MiniVault server's base URL, e.g. <c>https://minivault.local:8200</c>. Must be an absolute
    /// URL, and must use the <c>https</c> scheme unless <see cref="AllowInsecureHttp"/> is set.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The client id used to obtain an access token.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The client secret used to obtain an access token.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Directory used for the local secret cache. <c>null</c> disables the disk cache.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>The maximum age a cached secret may reach before it is treated as stale. Defaults to 7 days.</summary>
    public TimeSpan MaxCacheAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Interval at which cached secrets are refreshed in the background. <c>null</c> disables background
    /// refresh.</summary>
    public TimeSpan? RefreshInterval { get; set; }

    /// <summary>Optional certificate pin: the expected thumbprint of the server's TLS certificate.</summary>
    public string? ServerCertificateThumbprint { get; set; }

    /// <summary>The HTTP request timeout. Defaults to 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Optional sink for diagnostic log lines.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Allows <see cref="BaseUrl"/> to use a scheme other than <c>https</c>. Defaults to <c>false</c>.
    /// <para>
    /// This is a spec amendment: the original design mandated <c>https</c> unconditionally. It exists to support
    /// local/dev scenarios (e.g. a MiniVault instance reachable only over plain HTTP) without weakening the
    /// default posture, which still rejects a non-<c>https</c> <see cref="BaseUrl"/> unless this flag is set.
    /// </para>
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Validates the options. Throws <see cref="ArgumentException"/> when <see cref="BaseUrl"/>,
    /// <see cref="ClientId"/>, or <see cref="ClientSecret"/> is missing, when <see cref="BaseUrl"/> is not a
    /// well-formed absolute URL, when it does not use <c>https</c> and <see cref="AllowInsecureHttp"/> is not set,
    /// when <see cref="Timeout"/> is not positive, or when <see cref="ServerCertificateThumbprint"/> is set but
    /// does not normalize to exactly 40 hex characters (a SHA-1 thumbprint) — an unusable pin fails closed here
    /// rather than being silently skipped later.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) throw new ArgumentException("BaseUrl is required.", nameof(BaseUrl));
        if (string.IsNullOrWhiteSpace(ClientId)) throw new ArgumentException("ClientId is required.", nameof(ClientId));
        if (string.IsNullOrWhiteSpace(ClientSecret)) throw new ArgumentException("ClientSecret is required.", nameof(ClientSecret));

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("BaseUrl must be a well-formed absolute URL.", nameof(BaseUrl));

        if (!AllowInsecureHttp && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("BaseUrl must use https:// unless AllowInsecureHttp is set.", nameof(BaseUrl));

        if (Timeout <= TimeSpan.Zero) throw new ArgumentException("Timeout must be positive.", nameof(Timeout));

        if (!string.IsNullOrWhiteSpace(ServerCertificateThumbprint) &&
            MiniVaultClientFactory.NormalizeThumbprint(ServerCertificateThumbprint!).Length != 40)
        {
            throw new ArgumentException(
                "ServerCertificateThumbprint must normalize to exactly 40 hex characters (a SHA-1 thumbprint).",
                nameof(ServerCertificateThumbprint));
        }
    }
}
