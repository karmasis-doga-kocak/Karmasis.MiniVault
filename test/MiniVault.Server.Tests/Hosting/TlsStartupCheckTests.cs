using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Tests.Hosting;

public class TlsStartupCheckTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private readonly InMemoryMasterKeyProvider _provider = new();

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(migrate: false);
        await using var ctx = _db.CreateContext();
        await new VaultInitializer(ctx, _provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:MiniVault", _db.ConnectionString);
            b.ConfigureTestServices(s => s.AddSingleton<IMasterKeyProvider>(_provider));
            configure(b);
        });

    [Fact]
    public void DevelopmentCertificate_OutsideDevelopment_WithoutOverride_FailsToStart()
    {
        using var factory = CreateFactory(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Tls:AllowDevelopmentCertificate", "true");
        });

        var ex = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        ex.GetBaseException().Message.ShouldBe(
            "Tls:AllowDevelopmentCertificate is only allowed in the Development environment. Configure Tls:Certificate:Path or Tls:Certificate:Thumbprint.");
    }

    [Fact]
    public void DevelopmentCertificate_OutsideDevelopment_WithOverride_Starts()
    {
        using var factory = CreateFactory(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Tls:AllowDevelopmentCertificate", "true");
            b.UseSetting("Tls:AllowDevelopmentCertificateOutsideDevelopment", "true");
        });

        Should.NotThrow(() => factory.CreateClient());
    }
}
