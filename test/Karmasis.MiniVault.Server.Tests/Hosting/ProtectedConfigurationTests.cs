using Microsoft.Extensions.Configuration;
using Karmasis.MiniVault.Server.Hosting;

namespace Karmasis.MiniVault.Server.Tests.Hosting;

public class ProtectedConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Protect_ThenUnprotect_RoundTripsOnThisMachine()
    {
        if (!OperatingSystem.IsWindows()) return;

        var protectedValue = ProtectedConfiguration.Protect("Server=sql01;Database=MiniVault;User ID=u;Password='p;w'");

        protectedValue.ShouldNotContain("sql01");
        Convert.FromBase64String(protectedValue).Length.ShouldBeGreaterThan(0);
        ProtectedConfiguration.Unprotect(protectedValue).ShouldBe("Server=sql01;Database=MiniVault;User ID=u;Password='p;w'");
    }

    [Fact]
    public void Resolve_PrefersTheProtectedValueOverThePlainOne()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The binary's own appsettings.json always carries the plain LocalDB default; a machine configuration
        // that only writes the protected form must still win.
        var configuration = Config(
            ("ConnectionStrings:MiniVault", @"Server=(localdb)\MSSQLLocalDB;Database=MiniVault;Integrated Security=true"),
            ("ConnectionStrings:MiniVaultProtected", ProtectedConfiguration.Protect("Server=sql01;Database=MiniVault;Integrated Security=true")));

        ProtectedConfiguration.ResolveConnectionString(configuration).ShouldBe("Server=sql01;Database=MiniVault;Integrated Security=true");
    }

    [Fact]
    public void Resolve_FallsBackToThePlainValue()
    {
        var configuration = Config(("ConnectionStrings:MiniVault", "Server=plain;Database=MiniVault;Integrated Security=true"));

        ProtectedConfiguration.ResolveConnectionString(configuration).ShouldBe("Server=plain;Database=MiniVault;Integrated Security=true");
    }

    [Fact]
    public void Resolve_WithNeitherValue_SaysWhichKeysToSet()
    {
        var ex = Should.Throw<InvalidOperationException>(() => ProtectedConfiguration.ResolveConnectionString(Config()));

        ex.Message.ShouldContain("ConnectionStrings:MiniVault");
        ex.Message.ShouldContain("MiniVaultProtected");
    }

    [Fact]
    public void Resolve_WithAValueFromAnotherMachine_ExplainsHowToReproduceIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Not a DPAPI blob at all stands in for "protected on another host": both fail inside Unprotect.
        var configuration = Config(("ConnectionStrings:MiniVaultProtected", Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })));

        var ex = Should.Throw<InvalidOperationException>(() => ProtectedConfiguration.ResolveConnectionString(configuration));

        ex.Message.ShouldContain("minivault protect");
        ex.Message.ShouldContain("MiniVaultProtected");
    }
}
