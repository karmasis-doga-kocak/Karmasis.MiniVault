using System;

namespace Karmasis.MiniVault.Client;

/// <summary>Raised when a secret is served from the local cache instead of a live round trip to the server.</summary>
public sealed class CacheServedEventArgs : EventArgs
{
    /// <summary>Creates the event arguments for a cache hit.</summary>
    public CacheServedEventArgs(string name, bool stale, DateTimeOffset fetchedAt)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));

        Name = name;
        Stale = stale;
        FetchedAt = fetchedAt;
    }

    /// <summary>The secret's name.</summary>
    public string Name { get; }

    /// <summary>True when the cached value is older than <see cref="MiniVaultOptions.MaxCacheAge"/>.</summary>
    public bool Stale { get; }

    /// <summary>When the cached value was originally fetched from the server.</summary>
    public DateTimeOffset FetchedAt { get; }
}
