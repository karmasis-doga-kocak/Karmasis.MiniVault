using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MiniVault.Server.Keys;

/// <summary>Builds a protected (non-inherited) ACL granting FullControl only to SYSTEM, Administrators
/// and the current user. Used for files/directories that hold key material.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileAcl
{
    private static IdentityReference[] OwnerIdentities() =>
    [
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        WindowsIdentity.GetCurrent().User!,
    ];

    public static FileSecurity CreateOwnerOnly()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in OwnerIdentities())
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public static DirectorySecurity CreateOwnerOnlyDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in OwnerIdentities())
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
