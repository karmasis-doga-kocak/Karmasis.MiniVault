using System.Runtime.Versioning;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Tests.Keys;

public class DpapiMasterKeyProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "minivault-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void Store_ThenGetKek_RoundTrips_AndFileIsNotPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only
        var path = Path.Combine(_dir, "masterkey.bin");
        var provider = new DpapiMasterKeyProvider(path);
        var kek = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();

        provider.Exists().ShouldBeFalse();
        provider.Store(kek);

        provider.Exists().ShouldBeTrue();
        provider.GetKek().ShouldBe(kek);
        File.ReadAllBytes(path).ShouldNotBe(kek);
        File.ReadAllBytes(path).Length.ShouldBeGreaterThan(32);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void GetKek_Throws_WhenFileMissing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = new DpapiMasterKeyProvider(Path.Combine(_dir, "missing.bin"));

        Should.Throw<MasterKeyUnavailableException>(() => provider.GetKek());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void GetKek_Throws_WhenFileCorrupted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "masterkey.bin");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        var provider = new DpapiMasterKeyProvider(path);

        Should.Throw<MasterKeyUnavailableException>(() => provider.GetKek());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Store_RejectsWrongKeySize()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = new DpapiMasterKeyProvider(Path.Combine(_dir, "masterkey.bin"));

        Should.Throw<ArgumentException>(() => provider.Store(new byte[16]));
    }
}
