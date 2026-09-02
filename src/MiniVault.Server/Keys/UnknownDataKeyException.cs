namespace MiniVault.Server.Keys;

/// <summary>The requested data-key version is not in the loaded ring (and a reload did not produce it).</summary>
public sealed class UnknownDataKeyException(int version) : Exception($"No data key with version {version}.")
{
    public int Version { get; } = version;
}
