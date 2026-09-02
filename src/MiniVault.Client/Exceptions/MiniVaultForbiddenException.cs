namespace MiniVault.Client;

/// <summary>Thrown when the caller is authenticated but not permitted to perform the operation (HTTP 403).</summary>
public sealed class MiniVaultForbiddenException : MiniVaultException
{
    /// <summary>Creates the exception.</summary>
    public MiniVaultForbiddenException(string message, int? statusCode = null, string? errorCode = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
