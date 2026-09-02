namespace MiniVault.Server.Data.Entities;

public sealed class DataKey
{
    public int Version { get; set; }
    public byte[] WrappedByMaster { get; set; } = [];
    public byte[] WrappedByRecovery { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
