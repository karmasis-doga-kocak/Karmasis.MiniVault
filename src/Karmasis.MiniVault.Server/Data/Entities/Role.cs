namespace Karmasis.MiniVault.Server.Data.Entities;

public sealed class Role
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<RoleRule> Rules { get; set; } = [];
}
