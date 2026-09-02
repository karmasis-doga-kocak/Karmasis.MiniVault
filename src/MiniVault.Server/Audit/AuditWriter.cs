using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;

namespace MiniVault.Server.Audit;

/// <summary>
/// Appends audit rows. Each row is written on its own short-lived DbContext so the audit trail never shares a
/// change tracker with the request's mutating context: a failed write cannot drag its audit row into the same
/// rollback, and an audit row cannot flush a caller's half-built entity.
/// </summary>
public sealed class AuditWriter(IDbContextFactory<MiniVaultDbContext> factory, TimeProvider clock)
{
    public async Task WriteAsync(string clientId, string action, string? secretName, bool success, string? remoteIp, string? detail, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = clock.GetUtcNow(), ClientId = clientId, Action = action, SecretName = Truncate(secretName, Secret.MaxNameLength),
            Success = success, RemoteIp = remoteIp, Detail = Truncate(detail, 512),
        });
        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max) => value is not null && value.Length > max ? value[..max] : value;
}
