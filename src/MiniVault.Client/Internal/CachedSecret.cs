using System;

namespace MiniVault.Client.Internal;

/// <summary>A secret value as held in the in-memory or on-disk cache, along with when it was fetched.</summary>
internal sealed class CachedSecret
{
    /// <summary>
    /// Creates a cache entry. <paramref name="value"/> is copied, so the caller's array can be mutated or
    /// cleared afterwards without affecting the cached state.
    /// </summary>
    public CachedSecret(string name, byte[] value, string? contentType, int version, DateTimeOffset updatedAt, DateTimeOffset fetchedAt)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));

        Name = name;
        Value = (byte[])value.Clone();
        ContentType = contentType;
        Version = version;
        UpdatedAt = updatedAt;
        FetchedAt = fetchedAt;
    }

    /// <summary>The secret's name.</summary>
    public string Name { get; }

    /// <summary>The secret's raw value. This is the cache's own copy; do not mutate it in place.</summary>
    public byte[] Value { get; }

    /// <summary>Whatever content type the writer supplied, or <c>null</c> if none was supplied.</summary>
    public string? ContentType { get; }

    /// <summary>The version this value was stored as.</summary>
    public int Version { get; }

    /// <summary>When this version was written on the server.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>When this entry was fetched (or last confirmed) by the client.</summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>
    /// Converts this cache entry into the public <see cref="Secret"/> representation. The value is copied, so
    /// callers can freely mutate or clear the returned <see cref="Secret"/>'s bytes without corrupting this
    /// cache entry.
    /// </summary>
    public Secret ToSecret() => new Secret(Name, (byte[])Value.Clone(), ContentType, Version, UpdatedAt);
}
