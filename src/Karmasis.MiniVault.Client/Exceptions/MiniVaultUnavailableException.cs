using System;

namespace Karmasis.MiniVault.Client;

/// <summary>
/// Thrown when the server cannot be reached or reports itself unavailable: a network failure, a request timeout,
/// an HTTP 5xx response, or a 429 (rate limited, treated as retryable unavailability). When the failure originated
/// from an underlying exception (a network error or a timeout), that exception is preserved as
/// <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class MiniVaultUnavailableException : MiniVaultException
{
    /// <summary>Creates the exception.</summary>
    public MiniVaultUnavailableException(string message, Exception? innerException = null, int? statusCode = null, string? errorCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
