using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Karmasis.MiniVault.Server.Hosting;

/// <summary>
/// The DPAPI-protected form of <c>ConnectionStrings:MiniVault</c>. A Windows install writes
/// <c>ConnectionStrings:MiniVaultProtected</c> (base64 of the UTF-8 connection string protected with
/// DPAPI at LocalMachine scope and a fixed application entropy) instead of the plain string, so a SQL
/// login's password never sits in appsettings.json in clear text. The value is bound to the machine
/// that produced it: after a restore onto another host it has to be produced again (the installer
/// does it, and so does <c>minivault protect</c>).
/// Resolution order: the protected value when present, otherwise the plain value. The protected one
/// wins because the plain LocalDB default in the binary's own appsettings.json would otherwise shadow
/// a machine configuration that only carries the protected form.
/// </summary>
public static class ProtectedConfiguration
{
    public const string ConnectionStringName = "MiniVault";
    public const string ProtectedConnectionStringName = "MiniVaultProtected";

    /// <summary>Shared with the installer's custom actions and install.ps1; all three must agree.</summary>
    private static readonly byte[] Entropy = "Karmasis.MiniVault.Config.v1"u8.ToArray();

    [SupportedOSPlatform("windows")]
    public static string Protect(string plain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plain);
        var bytes = Encoding.UTF8.GetBytes(plain);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine));
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    [SupportedOSPlatform("windows")]
    public static string Unprotect(string protectedBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedBase64);
        var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64.Trim()), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>
    /// <c>ConnectionStrings:MiniVaultProtected</c> unprotected when it is set, else <c>ConnectionStrings:MiniVault</c>.
    /// Throws <see cref="InvalidOperationException"/> with an operator-readable message when neither is configured,
    /// when the protected value is not usable on this machine, or when it is set on a non-Windows host.
    /// </summary>
    public static string ResolveConnectionString(IConfiguration configuration)
    {
        var protectedValue = configuration.GetConnectionString(ProtectedConnectionStringName);
        if (!string.IsNullOrWhiteSpace(protectedValue))
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ProtectedConnectionStringName} is set, but DPAPI is only available on Windows. Configure ConnectionStrings:{ConnectionStringName} instead.");
            try
            {
                return Unprotect(protectedValue);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ProtectedConnectionStringName} could not be unprotected on this machine ({ex.GetType().Name}). " +
                    "It is bound to the machine that produced it: run the installer again, or set it from the output of " +
                    "'minivault protect --connection-string \"...\"' run on this host.", ex);
            }
        }

        var plain = configuration.GetConnectionString(ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(plain))
            return plain;

        throw new InvalidOperationException(
            $"ConnectionStrings:{ConnectionStringName} (or ConnectionStrings:{ProtectedConnectionStringName}) is not configured.");
    }
}
