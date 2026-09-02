using MiniVault.Server.Keys;

namespace MiniVault.Server.Vault;

/// <summary>Refuses to start the server unless the vault is initialized and the master key unwraps the data keys.</summary>
public sealed class VaultStartupCheck(DataKeyRing ring, ILogger<VaultStartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await ring.LoadAsync(ct);
        logger.LogInformation("Vault unlocked. Active data key version {Version}.", ring.ActiveVersion);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
