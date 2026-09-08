using System.Text.Json.Serialization;

namespace Karmasis.MiniVault.Contracts;

public sealed class TokenResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    /// <summary>Token lifetime in seconds.</summary>
    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }
}
