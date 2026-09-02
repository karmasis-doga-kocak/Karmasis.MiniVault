using System;

namespace MiniVault.Client.Internal;

/// <summary>A secret value as held in the in-memory or on-disk cache, along with when it was fetched.</summary>
internal sealed class CachedSecret
{
    public CachedSecret(string name, byte[] value, string? contentType, int version, DateTimeOffset updatedAt, DateTimeOffset fetchedAt)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));

        Name = name;
        Value = value;
        ContentType = contentType;
        Version = version;
        UpdatedAt = updatedAt;
        FetchedAt = fetchedAt;
    }

    /// <summary>The secret's name.</summary>
    public string Name { get; }

    /// <summary>The secret's raw value.</summary>
    public byte[] Value { get; }

    /// <summary>Whatever content type the writer supplied, or <c>null</c> if none was supplied.</summary>
    public string? ContentType { get; }

    /// <summary>The version this value was stored as.</summary>
    public int Version { get; }

    /// <summary>When this version was written on the server.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>When this entry was fetched (or last confirmed) by the client.</summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>Converts this cache entry into the public <see cref="Secret"/> representation.</summary>
    public Secret ToSecret() => new Secret(Name, Value, ContentType, Version, UpdatedAt);
}
