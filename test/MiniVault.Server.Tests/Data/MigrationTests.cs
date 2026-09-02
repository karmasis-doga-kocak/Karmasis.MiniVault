using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Tests.TestDoubles;

namespace MiniVault.Server.Tests.Data;

public class MigrationTests
{
    [Fact]
    public async Task InitialMigration_CreatesAllTables()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var ctx = db.CreateContext();

        var tables = await ctx.Database
            .SqlQueryRaw<string>("SELECT TABLE_NAME AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'")
            .ToListAsync();

        tables.ShouldContain("VaultMetadata");
        tables.ShouldContain("DataKeys");
        tables.ShouldContain("Secrets");
        tables.ShouldContain("Clients");
        tables.ShouldContain("Roles");
        tables.ShouldContain("RoleRules");
        tables.ShouldContain("ClientRoles");
        tables.ShouldContain("AuditLog");
        (await ctx.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }
}
