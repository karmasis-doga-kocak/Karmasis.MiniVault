using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Audit;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Cli;

/// <summary>Applies any pending EF Core migrations to the configured database. Safe to run against an uninitialized
/// database (it just creates the schema) and safe to run repeatedly (a no-op once everything is applied). Intended
/// to be run after an upgrade, before starting the service, so the schema matches the new binaries.</summary>
public static class MigrateCommand
{
    public static Command Build(Func<IServiceProvider> services, TextWriter output)
    {
        var command = new Command("migrate", "Apply pending database migrations. Run this after upgrading MiniVault, before starting the service.");
        command.SetAction(async (parseResult, ct) =>
        {
            using var scope = services().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MiniVaultDbContext>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            await db.Database.MigrateAsync(ct);

            // Recorded after MigrateAsync (the AuditLog table may not exist before it) and saved on its own, so a
            // failure to write the audit row cannot roll back schema changes that have already been applied. The
            // migration names are recorded rather than just a count: "which migrations ran" is the question an
            // operator asks after an upgrade.
            db.AuditLogs.Add(new AuditLog
            {
                Timestamp = clock.GetUtcNow(),
                ClientId = VaultInitializer.AuditClientId,
                Action = "migrate",
                Success = true,
                Detail = AuditWriter.TruncateDetail(string.Join(", ", pending)),
            });
            await db.SaveChangesAsync(ct);

            await output.WriteLineAsync(pending.Count == 0 ? "Database is up to date." : $"Applied {pending.Count} migration(s).");
            return 0;
        });
        return command;
    }
}
