using Microsoft.EntityFrameworkCore;

namespace MiniVault.Server.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "MiniVault";

    public static IServiceCollection AddMiniVaultData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.");
        services.AddDbContext<MiniVaultDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
        return services;
    }
}
