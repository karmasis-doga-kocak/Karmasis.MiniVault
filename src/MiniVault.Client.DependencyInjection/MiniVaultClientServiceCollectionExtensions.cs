using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MiniVault.Client;

namespace MiniVault.Client.DependencyInjection;

/// <summary>Registers a MiniVault client in an <see cref="IServiceCollection"/>.</summary>
public static class MiniVaultClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IMiniVaultClient"/> singleton configured by <paramref name="configure"/>.
    /// <para>
    /// The options are bound through <see cref="IOptions{TOptions}"/>, and the client is created lazily on
    /// first resolution via <see cref="MiniVaultClientFactory.Create(MiniVaultOptions)"/>. Because the client is
    /// registered as a singleton, it is disposed together with the container.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="configure">A delegate that sets the <see cref="MiniVaultOptions"/> used to create the client.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is <c>null</c>.</exception>
    public static IServiceCollection AddMiniVaultClient(
        this IServiceCollection services,
        Action<MiniVaultOptions> configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        services.Configure(configure);
        services.AddSingleton<IMiniVaultClient>(sp =>
            MiniVaultClientFactory.Create(sp.GetRequiredService<IOptions<MiniVaultOptions>>().Value));

        return services;
    }
}
