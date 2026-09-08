using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Karmasis.MiniVault.CustomActions.Tests
{
    /// <summary>
    /// A scratch folder that stands in for %ProgramData%\MiniVault. Disposal restores an ACL the
    /// (non-elevated) test process can delete, because DirectoryAcl.Protect strips inheritance and
    /// grants only SYSTEM and Administrators.
    /// </summary>
    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory(bool create = true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "minivault-ca-" + Guid.NewGuid().ToString("N"));

            if (create)
            {
                Directory.CreateDirectory(Path);
            }
        }

        public string Path { get; private set; }

        public string File(string name)
        {
            return System.IO.Path.Combine(Path, name);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            try
            {
                // The folder's owner keeps WRITE_DAC, so the protected ACL can always be undone here.
                var directory = new DirectoryInfo(Path);
                var security = directory.GetAccessControl();
                security.SetAccessRuleProtection(false, false);
                security.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                directory.SetAccessControl(security);
            }
            catch (UnauthorizedAccessException)
            {
                // Fall through to the delete attempt below; it reports the real problem.
            }

            Directory.Delete(Path, true);
        }
    }
}
