using System.Runtime.Versioning;
using System.Security.AccessControl;
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

    [Fact]
    [SupportedOSPlatform("windows")]
    public void GetKek_Throws_MasterKeyUnavailable_WhenFileLocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "masterkey.bin");
        var provider = new DpapiMasterKeyProvider(path);
        provider.Store(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Should.Throw<MasterKeyUnavailableException>(() => provider.GetKek());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Store_WritesFileWithProtectedAcl_NoUsersGroup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "masterkey.bin");
        var provider = new DpapiMasterKeyProvider(path);
        provider.Store(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var security = new FileInfo(path).GetAccessControl();
        security.AreAccessRulesProtected.ShouldBeTrue();
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        rules.ShouldNotContain(r => ((System.Security.Principal.SecurityIdentifier)r.IdentityReference).IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinUsersSid));
        rules.ShouldContain(r => ((System.Security.Principal.SecurityIdentifier)r.IdentityReference).IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid));
        provider.GetKek().Length.ShouldBe(32);   // current user can still read it back
    }
}
