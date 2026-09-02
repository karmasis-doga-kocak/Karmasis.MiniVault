using MiniVault.Server.Audit;
using MiniVault.Server.Auth;
using MiniVault.Server.Data;
using MiniVault.Server.Keys;
using MiniVault.Server.Secrets;
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
        services.AddScoped<VaultRecovery>();
        services.AddSingleton<DataKeyRing>();
        services.AddSingleton<SecretCipher>();
        services.AddScoped<SecretService>();
        services.AddScoped<AuditWriter>();
        services.AddScoped<ClientDirectory>();
        return services;
    }
}
