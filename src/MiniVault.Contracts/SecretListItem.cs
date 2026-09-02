using System;

namespace MiniVault.Contracts;

public sealed class SecretListItem
{
    public string Name { get; set; }
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
