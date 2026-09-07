using System;
using System.IO;

namespace Karmasis.MiniVault.CustomActions
{
    /// <summary>
    /// Puts the PFX the operator chose (MV_CERT_PATH) under %ProgramData%\MiniVault, next to
    /// appsettings.json. The server loads the file on every start, so it must not live at a path the
    /// operator may move or clean up later (a download folder, a share, a USB stick); under the
    /// protected folder it also picks up the folder ACL, which already grants the service account
    /// read access. The original is left untouched.
    /// </summary>
    public static class CertificateFileStaging
    {
        /// <summary>
        /// Copies <paramref name="sourcePath"/> into <paramref name="programDataDir"/> (keeping the file
        /// name, overwriting an older copy) and returns the path to write into Tls:Certificate:Path.
        /// A file already inside the folder is returned as is.
        /// </summary>
        /// <exception cref="FileNotFoundException">The source does not exist.</exception>
        public static string Stage(string sourcePath, string programDataDir)
        {
            if (string.IsNullOrEmpty(sourcePath))
            {
                throw new ArgumentException("sourcePath is required.", "sourcePath");
            }

            if (string.IsNullOrEmpty(programDataDir))
            {
                throw new ArgumentException("programDataDir is required.", "programDataDir");
            }

            var source = Path.GetFullPath(sourcePath);
            var targetDir = Path.GetFullPath(programDataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    "The PFX file '" + source + "' (MV_CERT_PATH) was not found.", source);
            }

            var sourceDir = Path.GetDirectoryName(source);
            if (sourceDir != null &&
                string.Equals(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), targetDir, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var destination = Path.Combine(targetDir, Path.GetFileName(source));
            File.Copy(source, destination, true);
            return destination;
        }
    }
}
