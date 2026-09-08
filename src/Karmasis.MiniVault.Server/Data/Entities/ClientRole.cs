namespace Karmasis.MiniVault.Server.Data.Entities;

public sealed class ClientRole
{
    public string ClientId { get; set; } = "";
    public string RoleName { get; set; } = "";
    public Client Client { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
