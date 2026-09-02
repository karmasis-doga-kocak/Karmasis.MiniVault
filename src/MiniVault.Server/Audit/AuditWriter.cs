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
            Success = success, RemoteIp = remoteIp, Detail = TruncateDetail(detail),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Length of the AuditLog.Detail column. Callers that add an audit row without going through
    /// <see cref="WriteAsync"/> (the CLI commands, which already own a DbContext) use
    /// <see cref="TruncateDetail"/> so a long detail is trimmed instead of failing the insert.</summary>
    public const int MaxDetailLength = 512;

    /// <summary>Trims <paramref name="detail"/> to <see cref="MaxDetailLength"/>.</summary>
    public static string? TruncateDetail(string? detail) => Truncate(detail, MaxDetailLength);

    private static string? Truncate(string? value, int max) => value is not null && value.Length > max ? value[..max] : value;
}
