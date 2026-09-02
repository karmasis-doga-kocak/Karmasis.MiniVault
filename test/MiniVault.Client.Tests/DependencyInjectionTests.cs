#if NET10_0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MiniVault.Client.DependencyInjection;

namespace MiniVault.Client.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddMiniVaultClient_RegistersSingleton()
    {
        var services = new ServiceCollection();

        services.AddMiniVaultClient(options =>
        {
            options.BaseUrl = "https://vault.test";
            options.ClientId = "c";
            options.ClientSecret = "s";
        });

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MiniVaultOptions>>().Value;
        options.BaseUrl.ShouldBe("https://vault.test");
        options.ClientId.ShouldBe("c");
        options.ClientSecret.ShouldBe("s");

        var first = provider.GetRequiredService<IMiniVaultClient>();
        var second = provider.GetRequiredService<IMiniVaultClient>();

        first.ShouldBeSameAs(second);
    }

    [Fact]
    public void AddMiniVaultClient_InvalidOptions_ThrowsOnResolve()
    {
        var services = new ServiceCollection();

        services.AddMiniVaultClient(options =>
        {
            options.BaseUrl = "https://vault.test";
            options.ClientId = "c";
            // ClientSecret intentionally left unset.
        });

        using var provider = services.BuildServiceProvider();

        var exception = Should.Throw<Exception>(() => provider.GetRequiredService<IMiniVaultClient>());

        var root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        root.ShouldBeOfType<ArgumentException>();
    }
}
#endif
