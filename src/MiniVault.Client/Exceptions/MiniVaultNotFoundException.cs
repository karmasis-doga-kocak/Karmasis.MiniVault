namespace MiniVault.Client;

/// <summary>Thrown when the requested secret does not exist (HTTP 404).</summary>
public sealed class MiniVaultNotFoundException : MiniVaultException
{
    /// <summary>Creates the exception.</summary>
    public MiniVaultNotFoundException(string message, int? statusCode = null, string? errorCode = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
