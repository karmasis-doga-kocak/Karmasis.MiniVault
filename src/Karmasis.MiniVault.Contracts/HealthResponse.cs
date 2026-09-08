using System.Text.Json.Serialization;

namespace Karmasis.MiniVault.Contracts;

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("initialized")]
    public bool Initialized { get; set; }

    [JsonPropertyName("activeDataKeyVersion")]
    public int ActiveDataKeyVersion { get; set; }
}
