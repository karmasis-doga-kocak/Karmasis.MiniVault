namespace MiniVault.Contracts;

public sealed class ErrorResponse
{
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string Conflict = "conflict";
    public const string VaultUnavailable = "vault_unavailable";

    public string Error { get; set; }
    public string Detail { get; set; }
}
