using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;

namespace MiniVault.Server.Audit;

public sealed class AuditWriter(MiniVaultDbContext db, TimeProvider clock)
{
    public async Task WriteAsync(string clientId, string action, string? secretName, bool success, string? remoteIp, string? detail, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = clock.GetUtcNow(), ClientId = clientId, Action = action, SecretName = secretName,
            Success = success, RemoteIp = remoteIp, Detail = detail is { Length: > 512 } ? detail[..512] : detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
