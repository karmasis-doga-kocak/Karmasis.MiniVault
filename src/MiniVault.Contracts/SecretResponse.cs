using System;

namespace MiniVault.Contracts;

public sealed class SecretResponse
{
    public string Name { get; set; }
    public string Value { get; set; }
    public string ContentType { get; set; }
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
