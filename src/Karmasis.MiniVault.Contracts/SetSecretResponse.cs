using System.Text.Json.Serialization;

namespace Karmasis.MiniVault.Contracts;

public sealed class SetSecretResponse
{
    [JsonPropertyName("version")]
    public int Version { get; set; }
}
