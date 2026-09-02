using System;

namespace MiniVault.Client;

/// <summary>Base type for all exceptions the MiniVault client throws.</summary>
public class MiniVaultException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public MiniVaultException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public MiniVaultException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>
    /// The HTTP status code returned by the server, when this exception originated from an HTTP response.
    /// Set by the constructor of the concrete exception type; it is not part of the public surface, so a caught
    /// exception cannot be rewritten before it is logged or re-thrown.
    /// </summary>
    public int? StatusCode { get; private protected set; }

    /// <summary>
    /// The server's error code (<see cref="MiniVault.Contracts.ErrorResponse.Error"/>), when available.
    /// Constructor-set, like <see cref="StatusCode"/>.
    /// </summary>
    public string? ErrorCode { get; private protected set; }
}
