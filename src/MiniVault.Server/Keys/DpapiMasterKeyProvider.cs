using System.Runtime.Versioning;
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
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(MiniVaultConfiguration.MachineConfigDirectory, DefaultFileName)
            : filePath;
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
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var protectedBytes = ProtectedData.Protect(kek, Entropy, DataProtectionScope.LocalMachine);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, FilePath, overwrite: true);
    }
}
