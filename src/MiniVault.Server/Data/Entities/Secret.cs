namespace MiniVault.Server.Data.Entities;

public sealed class Secret
{
    public const int MaxNameLength = 256;

    public string Name { get; set; } = "";
    public byte[] Ciphertext { get; set; } = [];
    public int DekVersion { get; set; }
    public string? ContentType { get; set; }
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public byte[] RowVersion { get; set; } = [];
}
