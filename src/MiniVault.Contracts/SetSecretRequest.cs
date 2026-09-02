using System.Text.Json.Serialization;

namespace MiniVault.Contracts;

public sealed class SetSecretRequest
{
    /// <summary>The secret value, base64-encoded. The server requires it, but it stays nullable here so a request
    /// that omits it is rejected as a missing value instead of silently storing an empty secret.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }
}
