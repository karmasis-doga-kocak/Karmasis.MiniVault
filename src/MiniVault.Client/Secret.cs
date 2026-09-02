using System;
using System.Text;

namespace MiniVault.Client;

/// <summary>A secret value retrieved from MiniVault, decoded from its wire representation.</summary>
public sealed class Secret
{
    /// <summary>Creates a secret from its already-decoded parts.</summary>
    public Secret(string name, byte[] value, string? contentType, int version, DateTimeOffset updatedAt)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));

        Name = name;
        Value = value;
        ContentType = contentType;
        Version = version;
        UpdatedAt = updatedAt;
    }

    /// <summary>The secret's name.</summary>
    public string Name { get; }

    /// <summary>The secret's raw value.</summary>
    public byte[] Value { get; }

    /// <summary>Whatever content type the writer supplied, or <c>null</c> if none was supplied.</summary>
    public string? ContentType { get; }

    /// <summary>The version this value was stored as.</summary>
    public int Version { get; }

    /// <summary>When this version was written.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Decodes <see cref="Value"/> as UTF-8 text.</summary>
    public string AsString() => Encoding.UTF8.GetString(Value);
}
