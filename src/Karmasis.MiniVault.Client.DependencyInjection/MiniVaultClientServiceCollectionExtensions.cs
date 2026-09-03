using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Karmasis.MiniVault.Client;

namespace Karmasis.MiniVault.Client.DependencyInjection;

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
    /// <para>
    /// The registration is a <c>TryAdd</c>: an <see cref="IMiniVaultClient"/> the application registered itself
    /// (a fake in a test host, a decorator) is left in place, and calling this twice does not end up building
    /// two clients. The <paramref name="configure"/> delegate is always applied, so repeated calls compose the
    /// way <see cref="OptionsServiceCollectionExtensions.Configure{TOptions}(IServiceCollection, Action{TOptions})"/>
    /// normally does.
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
        AddClient(services);

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IMiniVaultClient"/> singleton whose <see cref="MiniVaultOptions"/> are bound from
    /// a configuration section, e.g. <c>configuration.GetSection("MiniVault")</c>.
    /// <para>
    /// Binding follows the usual configuration rules: keys match property names case-insensitively,
    /// <c>Timeout</c>, <c>MaxCacheAge</c> and <c>RefreshInterval</c> accept the standard <see cref="TimeSpan"/>
    /// text form (<c>00:00:10</c>, <c>7.00:00:00</c>), and <c>Log</c> — being a delegate — cannot be bound from
    /// configuration; use the <see cref="AddMiniVaultClient(IServiceCollection, Action{MiniVaultOptions})"/>
    /// overload as well if you want one. Nothing in the section is validated at registration time; the options
    /// are validated when the client is first resolved, as with the delegate overload.
    /// </para>
    /// <para>
    /// Do not put <c>ClientSecret</c> in a plain configuration file. Bind the rest from configuration and supply
    /// the secret from a protected store (see <c>docs/client.md</c>, section 7).
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="section">The configuration section holding the MiniVault options.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="section"/> is <c>null</c>.</exception>
    public static IServiceCollection AddMiniVaultClient(
        this IServiceCollection services,
        IConfiguration section)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (section is null) throw new ArgumentNullException(nameof(section));

        services.Configure<MiniVaultOptions>(section);
        AddClient(services);

        return services;
    }

    private static void AddClient(IServiceCollection services) =>
        services.TryAddSingleton<IMiniVaultClient>(sp =>
            MiniVaultClientFactory.Create(sp.GetRequiredService<IOptions<MiniVaultOptions>>().Value));
}
