using Microsoft.EntityFrameworkCore;

namespace MiniVault.Server.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "MiniVault";

    public static IServiceCollection AddMiniVaultData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.");
        services.AddDbContext<MiniVaultDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)),
            optionsLifetime: ServiceLifetime.Singleton);
        // The factory hands out contexts that are independent of the request-scoped one (AuditWriter uses it), so an
        // audit row is never flushed together with a caller's pending changes. DbContextOptions is registered as a
        // singleton above because AddDbContextFactory's own registration is a TryAdd and its factory is a singleton.
        services.AddDbContextFactory<MiniVaultDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
        return services;
    }
}
