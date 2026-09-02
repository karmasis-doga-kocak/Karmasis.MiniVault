namespace MiniVault.Server.Auth;

public sealed class TokenOptions
{
    public const string SectionName = "Token";
    public int LifetimeMinutes { get; set; } = 15;
}
