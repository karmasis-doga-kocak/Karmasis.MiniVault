using Microsoft.EntityFrameworkCore;
using Karmasis.MiniVault.Server.Hosting;

namespace Karmasis.MiniVault.Server.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = ProtectedConfiguration.ConnectionStringName;

    public static IServiceCollection AddMiniVaultData(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved lazily, on first use of the options, not at registration: 'minivault protect' has to run on a
        // host whose ConnectionStrings:MiniVaultProtected is unusable (that is what it is for), and a bad value must
        // surface as the startup check's readable message rather than as a DI failure.
        var connectionString = new Lazy<string>(() => ProtectedConfiguration.ResolveConnectionString(configuration));
        services.AddDbContext<MiniVaultDbContext>(o => o.UseSqlServer(connectionString.Value, sql => sql.EnableRetryOnFailure(3)),
            optionsLifetime: ServiceLifetime.Singleton);
        // The factory hands out contexts that are independent of the request-scoped one (AuditWriter uses it), so an
        // audit row is never flushed together with a caller's pending changes. DbContextOptions is registered as a
        // singleton above because AddDbContextFactory's own registration is a TryAdd and its factory is a singleton.
        services.AddDbContextFactory<MiniVaultDbContext>(o => o.UseSqlServer(connectionString.Value, sql => sql.EnableRetryOnFailure(3)));
        return services;
    }
}
