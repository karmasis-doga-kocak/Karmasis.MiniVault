namespace MiniVault.Contracts;

public sealed class TokenResponse
{
    public string AccessToken { get; set; }
    public int ExpiresIn { get; set; }
}
