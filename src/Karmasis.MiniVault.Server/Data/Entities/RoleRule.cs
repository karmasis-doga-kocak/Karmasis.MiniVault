namespace Karmasis.MiniVault.Server.Data.Entities;

public sealed class RoleRule
{
    public int Id { get; set; }
    public string RoleName { get; set; } = "";
    /// <summary>Secret-name prefix, e.g. "dataskope/collector/".</summary>
    public string Scope { get; set; } = "";
    public Permission Permission { get; set; }
}
