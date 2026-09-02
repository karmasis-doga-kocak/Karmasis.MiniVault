using Microsoft.Extensions.Options;

namespace MiniVault.Server.Hosting;

/// <summary>
/// Validates <see cref="TlsOptions"/> — the settings Kestrel itself never looks at (a URL that is not https, a
/// store location that is not LocalMachine/CurrentUser, a thumbprint that is not 40 hex digits) — and re-states
/// the development-certificate rules, so a misconfigured install fails with a readable message.
/// <para>The certificate probe here is belt and braces, not the first line of defence: Kestrel materializes its
/// options (and therefore <see cref="KestrelConfiguration.Apply"/>'s <c>LoadCertificate</c> call) while the web
/// host's own hosted service starts, which is <em>before</em> this check runs. A bad PFX path or password
/// therefore already surfaced by then; loading it again costs nothing and keeps the check meaningful when the
/// server is hosted differently.</para>
/// Registered before <see cref="Vault.VaultStartupCheck"/> so TLS problems are reported first.
/// </summary>
public sealed class TlsStartupCheck(IOptions<TlsOptions> options, IHostEnvironment env, ILogger<TlsStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        var tls = options.Value;
        try
        {
            tls.Validate();
            if (tls.AllowDevelopmentCertificate && !env.IsDevelopment() && !tls.AllowDevelopmentCertificateOutsideDevelopment)
                throw new InvalidOperationException(KestrelConfiguration.DevelopmentCertificateNotAllowedMessage);
            if (tls.AllowDevelopmentCertificate && !env.IsDevelopment())
                logger.LogCritical("{Message}", KestrelConfiguration.DevelopmentCertificateOutsideDevelopmentMessage);
            if (!tls.AllowDevelopmentCertificate)
                KestrelConfiguration.LoadCertificate(tls.Certificate).Dispose();
        }
        catch (Exception ex)
        {
            // Logged without the exception object: Program.cs turns this into the process exit code and prints the
            // same one-line reason, and an operator reading a service log needs the reason, not a stack trace.
            logger.LogCritical("MiniVault cannot start: {Reason}", ex.Message);
            throw;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
