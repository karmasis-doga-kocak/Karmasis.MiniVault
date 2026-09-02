using Microsoft.Extensions.Options;

namespace MiniVault.Server.Keys;

public static class MasterKeyServiceCollectionExtensions
{
    public static IServiceCollection AddMasterKeyProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MasterKeyOptions>(configuration.GetSection(MasterKeyOptions.SectionName));
        services.AddSingleton<IMasterKeyProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MasterKeyOptions>>().Value;
            return options.Provider switch
            {
                var p when string.Equals(p, MasterKeyOptions.DpapiProvider, StringComparison.OrdinalIgnoreCase)
                    => OperatingSystem.IsWindows()
                        ? new DpapiMasterKeyProvider(options.Path)
                        : throw new InvalidOperationException("MasterKey:Provider=Dpapi requires Windows; use Environment."),
                var p when string.Equals(p, MasterKeyOptions.EnvironmentProvider, StringComparison.OrdinalIgnoreCase)
                    => new EnvironmentMasterKeyProvider(),
                _ => throw new InvalidOperationException($"Unknown MasterKey:Provider '{options.Provider}'. Use '{MasterKeyOptions.DpapiProvider}' or '{MasterKeyOptions.EnvironmentProvider}'.")
            };
        });
        return services;
    }
}
