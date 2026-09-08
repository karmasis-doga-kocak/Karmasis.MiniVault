namespace Karmasis.MiniVault.Client;

/// <summary>Thrown when the server rejects credentials or an access token (HTTP 401).</summary>
public sealed class MiniVaultAuthException : MiniVaultException
{
    /// <summary>Creates the exception.</summary>
    public MiniVaultAuthException(string message, int? statusCode = null, string? errorCode = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
