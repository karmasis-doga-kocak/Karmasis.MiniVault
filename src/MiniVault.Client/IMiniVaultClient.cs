using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiniVault.Contracts;

namespace MiniVault.Client;

/// <summary>A client for the MiniVault secret store.</summary>
public interface IMiniVaultClient : IDisposable
{
    /// <summary>Retrieves a secret by name.</summary>
    Task<Secret> GetSecretAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Writes a secret's value, creating it if it does not exist. Returns the resulting version.
    /// </summary>
    Task<int> SetSecretAsync(string name, byte[] value, string? contentType = null, CancellationToken ct = default);

    /// <summary>Deletes a secret.</summary>
    Task DeleteSecretAsync(string name, CancellationToken ct = default);

    /// <summary>Lists secrets whose name starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<SecretListItem>> ListSecretsAsync(string prefix, CancellationToken ct = default);

    /// <summary>Raised whenever a secret is served from the local cache instead of the server.</summary>
    event EventHandler<CacheServedEventArgs>? SecretServedFromCache;
}
