using System;
using System.Net.Http;
using System.Text;

namespace MiniVault.Client;

/// <summary>Creates <see cref="IMiniVaultClient"/> instances.</summary>
public static class MiniVaultClientFactory
{
    /// <summary>
    /// Creates a client over a handler built from <paramref name="options"/>: certificate pinning is enabled
    /// when <see cref="MiniVaultOptions.ServerCertificateThumbprint"/> is set, and the platform's normal
    /// certificate validation applies otherwise.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The options are not valid; see <see cref="MiniVaultOptions.Validate"/>.</exception>
    public static IMiniVaultClient Create(MiniVaultOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        options.Validate();

        return new MiniVaultClient(options, CreateHandler(options), () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates a client over a caller-supplied handler — for tests, or for hosting the client behind a custom
    /// handler chain (proxies, retries, instrumentation). Certificate pinning is the handler's responsibility
    /// in this overload. The handler is disposed together with the returned client.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="handler"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The options are not valid; see <see cref="MiniVaultOptions.Validate"/>.</exception>
    public static IMiniVaultClient Create(MiniVaultOptions options, HttpMessageHandler handler)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        options.Validate();

        return new MiniVaultClient(options, handler, () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Builds the handler used by <see cref="Create(MiniVaultOptions)"/>. When
    /// <see cref="MiniVaultOptions.ServerCertificateThumbprint"/> is set, a validation callback is installed
    /// that accepts the connection only when the presented certificate's thumbprint matches the configured one
    /// (case-insensitive; both sides are normalized to bare hex digits first). Without a thumbprint no callback
    /// is installed at all, so the platform's own validation stands — the client never accepts every certificate.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="MiniVaultOptions.ServerCertificateThumbprint"/> is set but does not normalize to exactly 40 hex
    /// characters (a SHA-1 thumbprint) — pinning is never silently skipped for an unusable value.
    /// </exception>
    internal static HttpClientHandler CreateHandler(MiniVaultOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        var handler = new HttpClientHandler();

        if (!string.IsNullOrWhiteSpace(options.ServerCertificateThumbprint))
        {
            var expected = NormalizeThumbprint(options.ServerCertificateThumbprint!);
            if (expected.Length != 40)
                throw new ArgumentException(
                    "ServerCertificateThumbprint must normalize to exactly 40 hex characters (a SHA-1 thumbprint).",
                    nameof(options));

            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                certificate is not null &&
                string.Equals(NormalizeThumbprint(certificate.Thumbprint), expected, StringComparison.OrdinalIgnoreCase);
        }

        return handler;
    }

    /// <summary>
    /// Keeps only ASCII hex digits, upper-cased, discarding everything else — <c>:</c> and <c>-</c> separators,
    /// spaces, and invisible characters such as U+200E (LEFT-TO-RIGHT MARK), which the Windows certificate MMC
    /// prepends when a thumbprint is copied from its property grid.
    /// </summary>
    internal static string NormalizeThumbprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))
                builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }
}
