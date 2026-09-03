namespace Karmasis.MiniVault.Client;

/// <summary>Thrown when the server rejects the request as malformed or conflicting (HTTP 400 or 409).</summary>
public sealed class MiniVaultRequestException : MiniVaultException
{
    /// <summary>Creates the exception.</summary>
    public MiniVaultRequestException(string message, int? statusCode = null, string? errorCode = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
