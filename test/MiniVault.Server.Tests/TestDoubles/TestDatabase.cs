using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data;

namespace MiniVault.Server.Tests.TestDoubles;

/// <summary>A throw-away LocalDB database per test class. Migrations are applied on creation; the DB is dropped on dispose.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public string DatabaseName { get; } = "MiniVaultTest_" + Guid.NewGuid().ToString("N");
    public string ConnectionString => $@"Server=(localdb)\MSSQLLocalDB;Database={DatabaseName};Integrated Security=true;TrustServerCertificate=true";

    public DbContextOptions<MiniVaultDbContext> Options =>
        new DbContextOptionsBuilder<MiniVaultDbContext>().UseSqlServer(ConnectionString).Options;

    public MiniVaultDbContext CreateContext() => new(Options);

    public static async Task<TestDatabase> CreateAsync(bool migrate = true)
    {
        var db = new TestDatabase();
        if (migrate)
        {
            await using var ctx = db.CreateContext();
            await ctx.Database.MigrateAsync();
        }
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureDeletedAsync();
    }
}
