using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Karmasis.MiniVault.Client.DependencyInjection;

namespace Karmasis.MiniVault.Client.Tests;

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

    [Fact]
    public void AddMiniVaultClient_DoesNotReplace_AnAlreadyRegisteredClient()
    {
        var services = new ServiceCollection();
        var own = new FakeClient();

        services.AddSingleton<IMiniVaultClient>(own);
        services.AddMiniVaultClient(options =>
        {
            options.BaseUrl = "https://vault.test";
            options.ClientId = "c";
            options.ClientSecret = "s";
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMiniVaultClient>().ShouldBeSameAs(own);
        provider.GetServices<IMiniVaultClient>().Count().ShouldBe(1);
    }

    [Fact]
    public void AddMiniVaultClient_FromConfigurationSection_BindsOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MiniVault:BaseUrl"] = "https://vault.test",
                ["MiniVault:ClientId"] = "dataskope-collector",
                ["MiniVault:ClientSecret"] = "s",
                ["MiniVault:CacheDirectory"] = @"C:\ProgramData\Dataskope\cache",
                ["MiniVault:MaxCacheAge"] = "2.00:00:00",
                ["MiniVault:RefreshInterval"] = "00:05:00",
                ["MiniVault:Timeout"] = "00:00:20",
                ["MiniVault:AllowInsecureHttp"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMiniVaultClient(configuration.GetSection("MiniVault"));

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MiniVaultOptions>>().Value;
        options.BaseUrl.ShouldBe("https://vault.test");
        options.ClientId.ShouldBe("dataskope-collector");
        options.ClientSecret.ShouldBe("s");
        options.CacheDirectory.ShouldBe(@"C:\ProgramData\Dataskope\cache");
        options.MaxCacheAge.ShouldBe(TimeSpan.FromDays(2));
        options.RefreshInterval.ShouldBe(TimeSpan.FromMinutes(5));
        options.Timeout.ShouldBe(TimeSpan.FromSeconds(20));
        options.AllowInsecureHttp.ShouldBeFalse();

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void AddMiniVaultClient_FromConfigurationSection_RegistersSingleton()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BaseUrl"] = "https://vault.test",
                ["ClientId"] = "c",
                ["ClientSecret"] = "s",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMiniVaultClient(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMiniVaultClient>().ShouldBeSameAs(provider.GetRequiredService<IMiniVaultClient>());
    }

    [Fact]
    public void AddMiniVaultClient_NullArguments_Throw()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Should.Throw<ArgumentNullException>(() => services.AddMiniVaultClient((Action<MiniVaultOptions>)null!));
        Should.Throw<ArgumentNullException>(() => services.AddMiniVaultClient((IConfiguration)null!));
        Should.Throw<ArgumentNullException>(() => ((IServiceCollection)null!).AddMiniVaultClient(configuration));
    }

    private sealed class FakeClient : IMiniVaultClient
    {
        public event EventHandler<CacheServedEventArgs>? SecretServedFromCache
        {
            add { }
            remove { }
        }

        public Task<Secret> GetSecretAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> SetSecretAsync(string name, byte[] value, string? contentType = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteSecretAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Contracts.SecretListItem>> ListSecretsAsync(string prefix, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }
}
