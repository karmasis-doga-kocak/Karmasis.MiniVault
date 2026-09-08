using System.Text.Json.Serialization;

namespace Karmasis.MiniVault.Contracts;

public sealed class ErrorResponse
{
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string Conflict = "conflict";
    public const string VaultUnavailable = "vault_unavailable";

    /// <summary>One of the constants above. Always present.</summary>
    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    /// <summary>Human-readable elaboration. Absent on responses that have nothing safe to add.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}
