using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Karmasis.MiniVault.CustomActions
{
    /// <summary>
    /// Applies the protected ACL of Step 3 of deploy/windows/install.ps1 to
    /// %ProgramData%\MiniVault: inheritance removed, then SYSTEM and BUILTIN\Administrators
    /// full control and (when the service does not run as LocalSystem) read/execute for the
    /// service account. Well-known SIDs are used instead of names, because the group names
    /// are localized on non-English Windows.
    /// </summary>
    public static class DirectoryAcl
    {
        private static readonly string[] LocalSystemAccounts =
        {
            "LocalSystem", "NT AUTHORITY\\SYSTEM", ".\\LocalSystem", "SYSTEM"
        };

        private static readonly string[] NetworkServiceAccounts =
        {
            "NetworkService", "NT AUTHORITY\\NetworkService"
        };

        private static readonly string[] LocalServiceAccounts =
        {
            "LocalService", "NT AUTHORITY\\LocalService"
        };

        public static bool IsLocalSystem(string serviceAccount)
        {
            return string.IsNullOrEmpty(serviceAccount) || Contains(LocalSystemAccounts, serviceAccount);
        }

        /// <summary>
        /// Resolves the service account to the identity that needs read access, or null when the
        /// service runs as LocalSystem (already covered by the SYSTEM ACE).
        /// </summary>
        public static IdentityReference ResolveServiceIdentity(string serviceAccount)
        {
            if (IsLocalSystem(serviceAccount))
            {
                return null;
            }

            if (Contains(NetworkServiceAccounts, serviceAccount))
            {
                return new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
            }

            if (Contains(LocalServiceAccounts, serviceAccount))
            {
                return new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
            }

            return new NTAccount(serviceAccount.Trim());
        }

        /// <summary>
        /// Creates <paramref name="path"/> if needed and replaces its ACL with the protected one.
        /// </summary>
        public static void Protect(string path, string serviceAccount)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("path is required.", "path");
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var directory = new DirectoryInfo(path);
            var security = new DirectorySecurity();

            // /inheritance:r - drop inherited ACEs instead of copying them in.
            security.SetAccessRuleProtection(true, false);

            const InheritanceFlags inherit = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            var serviceIdentity = ResolveServiceIdentity(serviceAccount);
            if (serviceIdentity != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    serviceIdentity,
                    FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            }

            directory.SetAccessControl(security);
        }

        /// <summary>
        /// The grants this class applies, in icacls notation. Used by the tests and the log line so
        /// the result can be compared with what install.ps1 prints.
        /// </summary>
        public static string[] DescribeGrants(string serviceAccount)
        {
            var grants = new List<string> { "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" };

            if (!IsLocalSystem(serviceAccount))
            {
                if (Contains(NetworkServiceAccounts, serviceAccount))
                {
                    grants.Add("*S-1-5-20:(OI)(CI)RX");
                }
                else if (Contains(LocalServiceAccounts, serviceAccount))
                {
                    grants.Add("*S-1-5-19:(OI)(CI)RX");
                }
                else
                {
                    grants.Add(serviceAccount.Trim() + ":(OI)(CI)RX");
                }
            }

            return grants.ToArray();
        }

        private static bool Contains(string[] candidates, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
