namespace Karmasis.MiniVault.Server.Auth;

public sealed class TokenOptions
{
    public const string SectionName = "Token";
    public int LifetimeMinutes { get; set; } = 15;

    /// <summary>Requests per minute accepted on /v1/auth/token, per server. Credential guessing is the only way in
    /// without a token, so the endpoint is capped even though every other endpoint is not.</summary>
    public int LoginRateLimitPerMinute { get; set; } = 30;
}
