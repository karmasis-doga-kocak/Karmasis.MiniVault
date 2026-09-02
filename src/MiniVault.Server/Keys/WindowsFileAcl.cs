using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MiniVault.Server.Keys;

/// <summary>Builds a protected (non-inherited) ACL granting FullControl to SYSTEM, Administrators and the
/// current user. Used for files/directories that hold key material.
/// <para>Both factories optionally take a directory whose <em>explicit</em> (non-inherited) Allow ACEs are
/// merged into the result. That is what keeps a grant an operator added out-of-band — e.g. the
/// <c>-ServiceAccount</c> grant <c>deploy/windows/install.ps1</c> puts on <c>%ProgramData%\MiniVault</c> —
/// from being dropped when the server re-protects the directory or writes the master key file. Only
/// inherited ACEs are removed; Deny ACEs are never copied.</para></summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileAcl
{
    /// <summary>Minimum rights an inherited-from-the-directory grant gets on the key file: enough to read
    /// the DPAPI blob back, never enough to overwrite it.</summary>
    private const FileSystemRights MinimumFileRights =
        FileSystemRights.Read | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions;

    private static IdentityReference[] OwnerIdentities() =>
    [
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        WindowsIdentity.GetCurrent().User!,
    ];

    public static FileSecurity CreateOwnerOnly(DirectoryInfo? parent = null)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in OwnerIdentities())
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        foreach (var rule in ExplicitAllowRules(parent))
        {
            // A file has no children: drop the inheritance flags and keep at least read access so the
            // service account the directory grants can still open the key file.
            security.AddAccessRule(new FileSystemAccessRule(
                rule.IdentityReference,
                rule.FileSystemRights | MinimumFileRights,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }

    public static DirectorySecurity CreateOwnerOnlyDirectory(DirectoryInfo? existing = null)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in OwnerIdentities())
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var rule in ExplicitAllowRules(existing))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                rule.IdentityReference,
                rule.FileSystemRights,
                rule.InheritanceFlags,
                rule.PropagationFlags,
                AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>The Allow ACEs written directly onto <paramref name="directory"/> (inherited ones excluded).
    /// Returns nothing when the directory is missing or its DACL cannot be read.</summary>
    private static List<FileSystemAccessRule> ExplicitAllowRules(DirectoryInfo? directory)
    {
        if (directory is null) return [];
        directory.Refresh();
        if (!directory.Exists) return [];

        AuthorizationRuleCollection rules;
        try
        {
            rules = directory.GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
            return [];
        }

        return rules.Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToList();
    }
}
