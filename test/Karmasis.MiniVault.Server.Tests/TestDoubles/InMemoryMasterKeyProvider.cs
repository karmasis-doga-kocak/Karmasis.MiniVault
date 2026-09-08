using Karmasis.MiniVault.Server.Keys;

namespace Karmasis.MiniVault.Server.Tests.TestDoubles;

public sealed class InMemoryMasterKeyProvider : IMasterKeyProvider
{
    private byte[]? _kek;
    public InMemoryMasterKeyProvider(byte[]? kek = null) => _kek = kek;
    public string Name => "InMemory";
    public bool CanStore => true;
    public bool Exists() => _kek is not null;
    public byte[] GetKek() => _kek is null ? throw new MasterKeyUnavailableException("No master key in memory.") : (byte[])_kek.Clone();
    public void Store(byte[] kek) => _kek = (byte[])kek.Clone();
}
