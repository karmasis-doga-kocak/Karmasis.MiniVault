using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Tests.Keys;

public class MasterKeyRegistrationTests
{
    private static IMasterKeyProvider Resolve(params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();
        var services = new ServiceCollection().AddMasterKeyProvider(config).BuildServiceProvider();
        return services.GetRequiredService<IMasterKeyProvider>();
    }

    [Fact]
    public void Environment_IsSelected() => Resolve(("MasterKey:Provider", "Environment")).ShouldBeOfType<EnvironmentMasterKeyProvider>();

    [Fact]
    public void Dpapi_IsSelected_WithCustomPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = Resolve(("MasterKey:Provider", "Dpapi"), ("MasterKey:Path", @"C:\temp\x.bin")).ShouldBeOfType<DpapiMasterKeyProvider>();
        provider.FilePath.ShouldBe(@"C:\temp\x.bin");
    }

    [Fact]
    public void Dpapi_DefaultPath_IsUnderProgramData()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = Resolve(("MasterKey:Provider", "Dpapi")).ShouldBeOfType<DpapiMasterKeyProvider>();
        provider.FilePath.ShouldEndWith(Path.Combine("MiniVault", "masterkey.bin"));
    }

    [Fact]
    public void UnknownProvider_Throws() => Should.Throw<InvalidOperationException>(() => Resolve(("MasterKey:Provider", "Hsm")));
}
