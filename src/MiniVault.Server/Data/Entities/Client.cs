namespace MiniVault.Server.Data.Entities;

public sealed class Client
{
    public string ClientId { get; set; } = "";
    public byte[] SecretHash { get; set; } = [];
    public byte[] SecretSalt { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public int SecretIterations { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<ClientRole> Roles { get; set; } = [];
}
