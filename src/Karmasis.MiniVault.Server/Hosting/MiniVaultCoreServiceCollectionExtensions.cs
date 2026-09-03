using Karmasis.MiniVault.Server.Audit;
using Karmasis.MiniVault.Server.Auth;
using Karmasis.MiniVault.Server.Data;
using Karmasis.MiniVault.Server.Keys;
using Karmasis.MiniVault.Server.Secrets;
using Karmasis.MiniVault.Server.Vault;

namespace Karmasis.MiniVault.Server.Hosting;

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
