using System.Text.Json.Serialization;

namespace Karmasis.MiniVault.Contracts;

public sealed class TokenRequest
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("clientSecret")]
    public string ClientSecret { get; set; } = "";
}
