using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karmasis.MiniVault.Server.Data;

public sealed class MiniVaultDbContextFactory : IDesignTimeDbContextFactory<MiniVaultDbContext>
{
    public MiniVaultDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MINIVAULT_DESIGN_CONNECTION")
            ?? @"Server=(localdb)\MSSQLLocalDB;Database=MiniVaultDesign;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<MiniVaultDbContext>().UseSqlServer(connectionString).Options;
        return new MiniVaultDbContext(options);
    }
}
