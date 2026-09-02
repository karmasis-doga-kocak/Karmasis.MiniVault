using System;

namespace MiniVault.Client;

/// <summary>Base type for all exceptions the MiniVault client throws.</summary>
public class MiniVaultException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public MiniVaultException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public MiniVaultException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>The HTTP status code returned by the server, when this exception originated from an HTTP response.</summary>
    public int? StatusCode { get; set; }

    /// <summary>The server's error code (<see cref="MiniVault.Contracts.ErrorResponse.Error"/>), when available.</summary>
    public string? ErrorCode { get; set; }
}
