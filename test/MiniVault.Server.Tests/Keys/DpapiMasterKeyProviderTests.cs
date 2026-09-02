using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Store_DoesNotReAclPreExistingForeignDirectoryNamedMiniVault()
    {
        if (!OperatingSystem.IsWindows()) return;
        var foreign = Path.Combine(_dir, "MiniVault");
        Directory.CreateDirectory(foreign);
        var before = new DirectoryInfo(foreign).GetAccessControl();
        var protectedBefore = before.AreAccessRulesProtected;
        var provider = new DpapiMasterKeyProvider(Path.Combine(foreign, "masterkey.bin"));

        provider.Store(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        new DirectoryInfo(foreign).GetAccessControl().AreAccessRulesProtected.ShouldBe(protectedBefore);
        new FileInfo(Path.Combine(foreign, "masterkey.bin")).GetAccessControl().AreAccessRulesProtected.ShouldBeTrue(); // the file itself is still protected
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Store_ProtectsDirectoryItCreates()
    {
        if (!OperatingSystem.IsWindows()) return;
        var created = Path.Combine(_dir, "fresh", "keys");
        var provider = new DpapiMasterKeyProvider(Path.Combine(created, "masterkey.bin"));

        provider.Store(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        new DirectoryInfo(created).GetAccessControl().AreAccessRulesProtected.ShouldBeTrue();
    }

    /// <summary>install.ps1 grants a custom -ServiceAccount read access to the config directory. Store must
    /// leave that grant alone and carry it onto masterkey.bin, or the service cannot read its own key.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Store_KeepsExplicitDirectoryGrant_AndPropagatesItToTheKeyFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var serviceAccount = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var dir = Directory.CreateDirectory(_dir);
        GrantReadExecute(dir, serviceAccount);

        var path = Path.Combine(_dir, "masterkey.bin");
        new DpapiMasterKeyProvider(path).Store(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        AllowRules(new DirectoryInfo(_dir).GetAccessControl())
            .ShouldContain(r => r.IdentityReference.Equals(serviceAccount));

        var fileSecurity = new FileInfo(path).GetAccessControl();
        fileSecurity.AreAccessRulesProtected.ShouldBeTrue();
        AllowRules(fileSecurity)
            .ShouldContain(r => r.IdentityReference.Equals(serviceAccount) && r.FileSystemRights.HasFlag(FileSystemRights.Read));
    }

    /// <summary>Re-protecting the machine config directory (the only directory Store re-ACLs when it already
    /// exists) must merge the explicit grants that are on it and drop only the inherited ACEs.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreateOwnerOnlyDirectory_MergesExplicitGrants_AndStaysProtected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var serviceAccount = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "config"));
        GrantReadExecute(dir, serviceAccount);

        dir.SetAccessControl(WindowsFileAcl.CreateOwnerOnlyDirectory(dir));

        var security = new DirectoryInfo(dir.FullName).GetAccessControl();
        security.AreAccessRulesProtected.ShouldBeTrue();
        var rules = AllowRules(security);
        rules.ShouldContain(r => r.IdentityReference.Equals(serviceAccount) && r.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute));
        rules.ShouldContain(r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid));
        rules.ShouldNotContain(r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.BuiltinUsersSid));
    }

    /// <summary>The merge that keeps a service-account grant must not keep a grant to <c>Everyone</c>: carrying it
    /// onto masterkey.bin would hand the DPAPI blob to every account on the machine. It is dropped from the key
    /// file and from the directory alike.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Store_DropsEveryoneGrant_FromTheKeyFileAndTheDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var serviceAccount = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var dir = Directory.CreateDirectory(_dir);
        GrantReadExecute(dir, everyone);
        GrantReadExecute(dir, serviceAccount);

        var path = Path.Combine(_dir, "masterkey.bin");
        new DpapiMasterKeyProvider(path).Store(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var fileRules = AllowRules(new FileInfo(path).GetAccessControl());
        fileRules.ShouldNotContain(r => r.IdentityReference.Equals(everyone));
        fileRules.ShouldContain(r => r.IdentityReference.Equals(serviceAccount)); // a specific account is still carried over

        // Store leaves a pre-existing directory's ACL alone; re-protecting the directory is what drops the grant.
        dir.SetAccessControl(WindowsFileAcl.CreateOwnerOnlyDirectory(dir));
        var directoryRules = AllowRules(new DirectoryInfo(_dir).GetAccessControl());
        directoryRules.ShouldNotContain(r => r.IdentityReference.Equals(everyone));
        directoryRules.ShouldContain(r => r.IdentityReference.Equals(serviceAccount));
    }

    [SupportedOSPlatform("windows")]
    private static void GrantReadExecute(DirectoryInfo dir, SecurityIdentifier identity)
    {
        var security = dir.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static List<FileSystemAccessRule> AllowRules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .ToList();
}
