using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Karmasis.MiniVault.Server.Auth;
using Karmasis.MiniVault.Server.Cli;
using Karmasis.MiniVault.Server.Data.Entities;
using Karmasis.MiniVault.Server.Keys;
using Karmasis.MiniVault.Server.Tests.TestDoubles;
using Karmasis.MiniVault.Server.Vault;

namespace Karmasis.MiniVault.Server.Tests.Cli;

public class ClientRoleCliTests : IAsyncLifetime
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

    private async Task<(int Code, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        var code = await CliApp.RunAsync([.. args, "--ConnectionStrings:MiniVault", _db.ConnectionString], output, s => s.AddSingleton<IMasterKeyProvider>(_provider));
        return (code, output.ToString());
    }

    private static string Line(string output, string prefix) =>
        output.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim();

    [Theory]
    [InlineData(new[] { "client", "list" }, true)]
    [InlineData(new[] { "role", "list" }, true)]
    public void IsCliInvocation_RecognizesClientAndRole(string[] args, bool expected) => CliApp.IsCliInvocation(args).ShouldBe(expected);

    [Fact]
    public async Task RoleAdd_Grant_List_RoundTrip()
    {
        (await Run("role", "add", "collector-reader", "--description", "reads collector secrets")).Code.ShouldBe(0);
        (await Run("role", "grant", "collector-reader", "--scope", "dataskope/collector/", "--permission", "read")).Code.ShouldBe(0);
        (await Run("role", "grant", "collector-reader", "--scope", "dataskope/collector/", "--permission", "write")).Code.ShouldBe(0); // upsert

        var (code, output) = await Run("role", "list");

        code.ShouldBe(0);
        output.ShouldContain("collector-reader: dataskope/collector/=Write");
        output.ShouldNotContain("=Read");
    }

    [Fact]
    public async Task ClientAdd_PrintsSecret_AndSecretAuthenticates()
    {
        await Run("role", "add", "r1");
        await Run("role", "grant", "r1", "--scope", "a/", "--permission", "read");

        var (code, output) = await Run("client", "add", "collector-1", "--role", "r1");

        code.ShouldBe(0);
        output.ShouldContain("Client created: collector-1");
        var secret = Line(output, "Client secret:");
        Convert.FromBase64String(secret).Length.ShouldBe(32);
        output.ShouldContain("not shown again");

        await using var ctx = _db.CreateContext();
        var identity = await new ClientDirectory(ctx, TimeProvider.System).AuthenticateAsync("collector-1", secret, CancellationToken.None);
        identity.ShouldNotBeNull();
        identity!.Roles.ShouldBe(["r1"]);
        (await ctx.AuditLogs.CountAsync(a => a.Action == "client.add" && a.ClientId == "cli")).ShouldBe(1);
    }

    [Fact]
    public async Task ClientAdd_UnknownRole_IsError()
    {
        var (code, output) = await Run("client", "add", "c", "--role", "nope");

        code.ShouldBe(1);
        output.ShouldContain("Error:");
        output.ShouldContain("nope");
    }

    [Fact]
    public async Task ClientAssign_ThenList_ShowsRole()
    {
        await Run("role", "add", "r1"); await Run("role", "add", "r2");
        await Run("client", "add", "c1", "--role", "r1");

        (await Run("client", "assign", "c1", "--role", "r2")).Code.ShouldBe(0);
        var (code, output) = await Run("client", "list");

        code.ShouldBe(0);
        output.ShouldContain("c1 [enabled]: r1, r2");
    }

    [Fact]
    public async Task RoleRemove_ThenClientList_ShowsNoRole()
    {
        await Run("role", "add", "r1");
        await Run("client", "add", "c1", "--role", "r1");

        (await Run("role", "remove", "r1")).Code.ShouldBe(0);
        var (_, output) = await Run("client", "list");

        output.ShouldContain("c1 [enabled]: (no roles)");
    }

    [Fact]
    public async Task RoleGrant_EmptyScope_RequiresAll()
    {
        await Run("role", "add", "r1");

        var (code, output) = await Run("role", "grant", "r1", "--scope", "", "--permission", "read");

        code.ShouldBe(1);
        output.ShouldContain("--all");

        (await Run("role", "grant", "r1", "--all", "--permission", "read")).Code.ShouldBe(0);
        (await Run("role", "list")).Output.ShouldContain("r1: =Read");
    }

    [Fact]
    public async Task RoleGrant_InvalidScope_IsError()
    {
        await Run("role", "add", "r1");

        var (code, output) = await Run("role", "grant", "r1", "--scope", "dataskope/%", "--permission", "read");

        code.ShouldBe(1);
        output.ShouldContain("Error:");
        (await Run("role", "list")).Output.ShouldContain("r1: (no rules)");
    }

    [Fact]
    public async Task ClientDisable_ThenAuthenticateFails_AndEnableRestores()
    {
        await Run("role", "add", "r1");
        var (_, addOutput) = await Run("client", "add", "c1", "--role", "r1");
        var secret = Line(addOutput, "Client secret:");

        (await Run("client", "disable", "c1")).Code.ShouldBe(0);
        await using (var disabled = _db.CreateContext())
            (await new ClientDirectory(disabled, TimeProvider.System).AuthenticateAsync("c1", secret, CancellationToken.None)).ShouldBeNull();
        (await Run("client", "list")).Output.ShouldContain("c1 [disabled]");

        (await Run("client", "enable", "c1")).Code.ShouldBe(0);
        await using (var enabled = _db.CreateContext())
            (await new ClientDirectory(enabled, TimeProvider.System).AuthenticateAsync("c1", secret, CancellationToken.None)).ShouldNotBeNull();

        await using var audit = _db.CreateContext();
        (await audit.AuditLogs.CountAsync(a => a.Action == "client.disable")).ShouldBe(1);
        (await audit.AuditLogs.CountAsync(a => a.Action == "client.enable")).ShouldBe(1);
    }

    [Fact]
    public async Task ClientRemove_ThenAuthenticateFails()
    {
        await Run("role", "add", "r1");
        var (_, addOutput) = await Run("client", "add", "c1", "--role", "r1");
        var secret = Line(addOutput, "Client secret:");

        (await Run("client", "remove", "c1")).Code.ShouldBe(0);

        await using var ctx = _db.CreateContext();
        (await new ClientDirectory(ctx, TimeProvider.System).AuthenticateAsync("c1", secret, CancellationToken.None)).ShouldBeNull();
    }
}
