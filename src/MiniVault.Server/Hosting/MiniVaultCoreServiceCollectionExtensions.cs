using MiniVault.Server.Data;
using MiniVault.Server.Keys;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Hosting;

public static class MiniVaultCoreServiceCollectionExtensions
{
    /// <summary>Everything both the CLI and the server need: data access, master key provider, clock, vault services.</summary>
    public static IServiceCollection AddMiniVaultCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMiniVaultData(configuration);
        services.AddMasterKeyProvider(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<VaultInitializer>();
        services.AddSingleton<DataKeyRing>();
        return services;
    }
}
