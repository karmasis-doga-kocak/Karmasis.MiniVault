using Karmasis.MiniVault.Server.Keys;

namespace Karmasis.MiniVault.Server.Vault;

/// <summary>Refuses to start the server unless the vault is initialized and the master key unwraps the data keys.</summary>
public sealed class VaultStartupCheck(DataKeyRing ring, ILogger<VaultStartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await ring.LoadAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "MiniVault cannot start: {Reason}", ex.Message);
            throw;
        }
        logger.LogInformation("Vault unlocked. Active data key version {Version}.", ring.ActiveVersion);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
