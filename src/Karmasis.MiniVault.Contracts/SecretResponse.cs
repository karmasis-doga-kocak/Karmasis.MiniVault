using System;
using System.Text.Json.Serialization;

namespace Karmasis.MiniVault.Contracts;

public sealed class SecretResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The secret value, base64-encoded.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    /// <summary>Whatever the writer supplied, or null if it supplied nothing.</summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}
