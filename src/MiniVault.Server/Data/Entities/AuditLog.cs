namespace MiniVault.Server.Data.Entities;

public sealed class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string ClientId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? SecretName { get; set; }
    public bool Success { get; set; }
    public string? RemoteIp { get; set; }
    public string? Detail { get; set; }
}
