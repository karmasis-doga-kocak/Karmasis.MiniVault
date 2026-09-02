using System;
using System.Text.Json.Serialization;

namespace MiniVault.Contracts;

public sealed class SecretListItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}
