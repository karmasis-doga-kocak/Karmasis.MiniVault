using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using MiniVault.Server.Hosting;

namespace MiniVault.Server.Keys;

/// <summary>Stores the KEK in a file protected with DPAPI (LocalMachine scope). Windows service scenario.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiMasterKeyProvider : IMasterKeyProvider
{
    public const string DefaultFileName = "masterkey.bin";
    private static readonly byte[] Entropy = "Karmasis.MiniVault.MasterKey.v1"u8.ToArray();

    public DpapiMasterKeyProvider(string? filePath = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Dpapi master key provider requires Windows. Use MasterKey:Provider=Environment on Linux.");
        var resolved = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(MiniVaultConfiguration.MachineConfigDirectory, DefaultFileName)
            : filePath;
        FilePath = Path.GetFullPath(resolved);
    }

    public string FilePath { get; }
    public string Name => MasterKeyOptions.DpapiProvider;
    public bool CanStore => true;

    public bool Exists() => File.Exists(FilePath);

    public byte[] GetKek()
    {
        if (!File.Exists(FilePath))
            throw new MasterKeyUnavailableException($"Master key file not found: {FilePath}. Run 'minivault init' first.");
        try
        {
            var kek = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.LocalMachine);
            if (kek.Length != MasterKey.Size)
                throw new MasterKeyUnavailableException($"Master key file {FilePath} has an unexpected length.");
            return kek;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new MasterKeyUnavailableException($"Master key file {FilePath} could not be read or unprotected ({ex.GetType().Name}). It may be locked, inaccessible, corrupted, or created on another machine.", ex);
        }
    }

    public void Store(byte[] kek)
    {
        MasterKey.ValidateSize(kek, nameof(kek));
        var directory = Path.GetDirectoryName(FilePath)!;
        var dirInfo = new DirectoryInfo(directory);
        var directoryExisted = dirInfo.Exists;
        if (!directoryExisted) dirInfo.Create();
        dirInfo.Refresh();
        // Only reset the ACL for a directory we created ourselves, or for the well-known machine
        // config directory. A pre-existing directory the caller pointed MasterKey:Path at (even one
        // that happens to be named "MiniVault") must keep whatever ACL it already had.
        // When we do re-protect the machine config directory, the explicit grants already on it are
        // merged in: install.ps1 grants a custom -ServiceAccount there, and stripping that would leave
        // the service unable to read its own configuration and key file.
        if (ShouldProtectDirectory(dirInfo, created: !directoryExisted))
        {
            dirInfo.SetAccessControl(WindowsFileAcl.CreateOwnerOnlyDirectory(directoryExisted ? dirInfo : null));
            dirInfo.Refresh();
        }

        var protectedBytes = ProtectedData.Protect(kek, Entropy, DataProtectionScope.LocalMachine);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Carry the directory's explicit grants (read-only) onto the key file itself, so a service
            // account granted access to the directory can actually open masterkey.bin.
            var fileSecurity = WindowsFileAcl.CreateOwnerOnly(dirInfo);
            using (var stream = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, fileSecurity))
                stream.Write(protectedBytes, 0, protectedBytes.Length);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            Array.Clear(protectedBytes);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool ShouldProtectDirectory(DirectoryInfo dir, bool created)
    {
        if (created) return true;
        var actual = Path.GetFullPath(dir.FullName).TrimEnd(Path.DirectorySeparatorChar);
        var machineConfig = Path.GetFullPath(MiniVaultConfiguration.MachineConfigDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(actual, machineConfig, StringComparison.OrdinalIgnoreCase);
    }
}
