using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Karmasis.MiniVault.Server.Auth;
using Karmasis.MiniVault.Server.Data;
using Karmasis.MiniVault.Server.Data.Entities;
using Karmasis.MiniVault.Server.Tests.TestDoubles;

namespace Karmasis.MiniVault.Server.Tests.Auth;

public class ClientDirectoryTests : IAsyncLifetime
{
    private VaultFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await VaultFixture.CreateAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task AddClient_ReturnsSecret_Authenticate_Succeeds_WithRoles()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
        await sut.AddRoleAsync("reader", null, CancellationToken.None);

        var secret = await sut.AddClientAsync("collector-1", ["reader"], CancellationToken.None);
        secret.ShouldNotBeNullOrWhiteSpace();

        var identity = await sut.AuthenticateAsync("collector-1", secret, CancellationToken.None);

        identity.ShouldNotBeNull();
        identity!.ClientId.ShouldBe("collector-1");
        identity.Roles.ShouldBe(["reader"]);
    }

    [Fact]
    public async Task Authenticate_WrongSecret_ReturnsNull()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
        await sut.AddClientAsync("c1", [], CancellationToken.None);

        var identity = await sut.AuthenticateAsync("c1", "wrong-secret", CancellationToken.None);

        identity.ShouldBeNull();
    }

    [Fact]
    public async Task Authenticate_Disabled_ReturnsNull()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
        var db = scope.ServiceProvider.GetRequiredService<MiniVaultDbContext>();
        var secret = await sut.AddClientAsync("c1", [], CancellationToken.None);
        var client = await db.Clients.SingleAsync(c => c.ClientId == "c1", CancellationToken.None);
        client.Enabled = false;
        await db.SaveChangesAsync(CancellationToken.None);

        var identity = await sut.AuthenticateAsync("c1", secret, CancellationToken.None);

        identity.ShouldBeNull();
    }

    [Fact]
    public async Task AddClient_UnknownRole_Throws()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();

        var ex = await Should.ThrowAsync<ArgumentException>(() => sut.AddClientAsync("c1", ["ghost"], CancellationToken.None));
        ex.Message.ShouldContain("ghost");
    }

    [Fact]
    public async Task AddClient_Duplicate_Throws()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
        await sut.AddClientAsync("c1", [], CancellationToken.None);

        var ex = await Should.ThrowAsync<ArgumentException>(() => sut.AddClientAsync("c1", [], CancellationToken.None));
        ex.Message.ShouldContain("c1");
    }

    [Fact]
    public async Task GrantAsync_UpsertsRule()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
        await sut.AddRoleAsync("reader", null, CancellationToken.None);

        await sut.GrantAsync("reader", "app/", Permission.Read, CancellationToken.None);
        await sut.GrantAsync("reader", "app/", Permission.Write, CancellationToken.None);

        var roles = await sut.ListRolesAsync(CancellationToken.None);
        var role = roles.Single(r => r.Name == "reader");
        role.Rules.Count.ShouldBe(1);
        role.Rules.Single().Permission.ShouldBe(Permission.Write);
    }

    [Fact]
    public async Task RemoveRole_CascadesRulesAndAssignments()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
        var db = scope.ServiceProvider.GetRequiredService<MiniVaultDbContext>();
        await sut.AddRoleAsync("reader", null, CancellationToken.None);
        await sut.GrantAsync("reader", "app/", Permission.Read, CancellationToken.None);
        await sut.AddClientAsync("c1", ["reader"], CancellationToken.None);

        await sut.RemoveRoleAsync("reader", CancellationToken.None);

        (await db.Roles.AnyAsync(r => r.Name == "reader", CancellationToken.None)).ShouldBeFalse();
        (await db.RoleRules.AnyAsync(r => r.RoleName == "reader", CancellationToken.None)).ShouldBeFalse();
        (await db.ClientRoles.AnyAsync(cr => cr.RoleName == "reader", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task GetRules_ReturnsRulesForGivenRolesOnly()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
        await sut.AddRoleAsync("reader", null, CancellationToken.None);
        await sut.AddRoleAsync("writer", null, CancellationToken.None);
        await sut.GrantAsync("reader", "app/", Permission.Read, CancellationToken.None);
        await sut.GrantAsync("writer", "app/", Permission.Write, CancellationToken.None);

        var rules = await sut.GetRulesAsync(["reader"], CancellationToken.None);

        rules.Count.ShouldBe(1);
        rules.Single().RoleName.ShouldBe("reader");
    }

    [Fact]
    public async Task ListClients_IncludesRoles()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ClientDirectory>();
        await sut.AddRoleAsync("reader", null, CancellationToken.None);
        await sut.AddClientAsync("c1", ["reader"], CancellationToken.None);
        await sut.AddClientAsync("c2", [], CancellationToken.None);

        var clients = await sut.ListClientsAsync(CancellationToken.None);

        clients.Single(c => c.ClientId == "c1").Roles.ShouldBe(["reader"]);
        clients.Single(c => c.ClientId == "c2").Roles.ShouldBeEmpty();
        clients.ShouldAllBe(c => c.Enabled);
    }
}
