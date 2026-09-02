using Microsoft.Extensions.Options;

namespace MiniVault.Server.Hosting;

/// <summary>
/// Validates <see cref="TlsOptions"/> and, unless a development certificate is allowed, loads the configured
/// certificate once (immediately discarding it) so a misconfigured install — a bad PFX path/password, or a
/// thumbprint that is not installed — fails fast with a readable message instead of only failing once a client
/// connects. Registered before <see cref="Vault.VaultStartupCheck"/> so TLS problems are reported first.
/// </summary>
public sealed class TlsStartupCheck(IOptions<TlsOptions> options, ILogger<TlsStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        var tls = options.Value;
        try
        {
            tls.Validate();
            if (!tls.AllowDevelopmentCertificate)
                KestrelConfiguration.LoadCertificate(tls.Certificate).Dispose();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "MiniVault cannot start: {Reason}", ex.Message);
            throw;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
