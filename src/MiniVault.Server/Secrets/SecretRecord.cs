namespace MiniVault.Server.Secrets;

public sealed record SecretRecord(string Name, byte[] Value, string? ContentType, int Version, DateTimeOffset UpdatedAt);
