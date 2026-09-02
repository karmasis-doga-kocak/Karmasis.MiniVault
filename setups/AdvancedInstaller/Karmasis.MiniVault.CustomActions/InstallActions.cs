using System;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using Karmasis.AdvancedInstallerKit;

namespace Karmasis.MiniVault.CustomActions
{
    /// <summary>Return codes understood by Advanced Installer's DotNetMethodCaller.</summary>
    public enum ActionResult
    {
        Success = 0,
        Failure = 1
    }

    /// <summary>
    /// The MSI properties handed to the deferred custom actions through CustomActionData.
    /// The names must match the AI_DATA_SETTER rows in Karmasis.MiniVault.aip.
    /// </summary>
    public class InstallModel
    {
        [InstallArgument("MV_CONNECTIONSTRING")]
        public string ConnectionString { get; set; }

        [InstallArgument("MV_SERVICEACCOUNT")]
        public string ServiceAccount { get; set; }

        [InstallArgument("MV_RECOVERY")]
        public string Recovery { get; set; }

        [InstallArgument("MV_SHARES")]
        public int Shares { get; set; }

        [InstallArgument("MV_THRESHOLD")]
        public int Threshold { get; set; }

        [InstallArgument("MV_MASTERKEY")]
        public string MasterKey { get; set; }

        [InstallArgument("MV_CERT_PATH")]
        public string CertificatePath { get; set; }

        [InstallArgument("MV_CERT_PASSWORD")]
        public string CertificatePassword { get; set; }

        [InstallArgument("MV_CERT_THUMBPRINT")]
        public string CertificateThumbprint { get; set; }

        [InstallArgument("MV_URL")]
        public string Url { get; set; }

        /// <summary>[APPDIR] - the install directory holding minivault.exe.</summary>
        [InstallArgument("APPDIR")]
        public string AppDir { get; set; }

        /// <summary>[CommonAppDataFolder]MiniVault - where appsettings.json and the DPAPI key live.</summary>
        [InstallArgument("MV_PROGRAMDATA")]
        public string ProgramDataDir { get; set; }
    }

    /// <summary>
    /// Custom actions for the MiniVault MSI. Each public entry point takes the MSI session handle
    /// passed by Advanced Installer's DotNetMethodCaller and returns an <see cref="ActionResult"/>
    /// as an int; the internal overloads take an <see cref="IMsiSession"/> so they can be tested.
    /// The steps mirror deploy/windows/install.ps1.
    /// </summary>
    public static class InstallActions
    {
        internal const string SqlOkProperty = "MV_SQL_OK";
        internal const string SqlErrorProperty = "MV_SQL_ERROR";
        internal const int SqlConnectTimeoutSeconds = 5;

        // -------------------------------------------------------------------
        // WriteMachineConfig (deferred, NoImpersonate)
        // -------------------------------------------------------------------

        /// <summary>
        /// Writes %ProgramData%\MiniVault\appsettings.json from the MV_* properties and locks the
        /// folder down with a protected ACL. Steps 2 and 3 of install.ps1.
        /// </summary>
        public static int WriteMachineConfig(string sessionHandle)
        {
            return WriteMachineConfig(new MsiSession(sessionHandle));
        }

        internal static int WriteMachineConfig(IMsiSession session)
        {
            try
            {
                var model = session.MapCustomActionData<InstallModel>();
                var programDataDir = ResolveProgramDataDir(model);
                var configPath = Path.Combine(programDataDir, "appsettings.json");

                var config = new MachineConfig
                {
                    ConnectionString = model.ConnectionString,
                    MasterKeyProvider = MachineConfigWriter.DefaultMasterKeyProvider,
                    Url = model.Url,
                    CertificatePath = NullIfBlank(model.CertificatePath),
                    CertificatePassword = model.CertificatePassword,
                    CertificateThumbprint = NullIfBlank(model.CertificateThumbprint)
                };

                MachineConfigWriter.Write(configPath, config);
                DirectoryAcl.Protect(programDataDir, model.ServiceAccount);

                session.SendMessage(
                    string.Format(CultureInfo.InvariantCulture,
                        "MiniVault: wrote {0} and applied a protected ACL ({1}).",
                        configPath, string.Join(" ", DirectoryAcl.DescribeGrants(model.ServiceAccount))),
                    InstallMessage.INFO);

                return (int)ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.SendMessage(
                    "MiniVault: failed to write the machine configuration: " + ex.Message,
                    InstallMessage.ERROR);
                return (int)ActionResult.Failure;
            }
        }

        // -------------------------------------------------------------------
        // RunInit (deferred, NoImpersonate)
        // -------------------------------------------------------------------

        /// <summary>
        /// Runs 'minivault.exe init' and leaves the recovery material in
        /// %ProgramData%\MiniVault\recovery-&lt;timestamp&gt;.txt. Step 4 of install.ps1.
        /// A deferred custom action cannot set MSI properties, so the material is NOT handed back to
        /// the UI: the operator must open the file the INFO message names, copy the material to a
        /// safe offline location and delete the file.
        /// </summary>
        public static int RunInit(string sessionHandle)
        {
            return RunInit(new MsiSession(sessionHandle), new ProcessRunner());
        }

        internal static int RunInit(IMsiSession session, IProcessRunner processRunner)
        {
            try
            {
                var model = session.MapCustomActionData<InstallModel>();
                var programDataDir = ResolveProgramDataDir(model);

                if (string.IsNullOrEmpty(model.AppDir))
                {
                    session.SendMessage("MiniVault: APPDIR is not set, cannot locate minivault.exe.", InstallMessage.ERROR);
                    return (int)ActionResult.Failure;
                }

                if (!Directory.Exists(programDataDir))
                {
                    Directory.CreateDirectory(programDataDir);
                }

                var exePath = Path.Combine(model.AppDir, MiniVaultCli.ExecutableName);
                var outFile = Path.Combine(programDataDir, "recovery-" +
                    DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".txt");

                var arguments = MiniVaultCli.BuildInitArguments(
                    model.Recovery, model.Shares, model.Threshold, model.MasterKey, outFile);

                var result = processRunner.Run(exePath, arguments);

                if (result.ExitCode != 0)
                {
                    // Upgrades re-run RunInit against an already-initialized vault (the deferred
                    // action has no way to know in advance). The server reports that with
                    // VaultAlreadyInitializedException, surfaced by the CLI as an "already
                    // initialized" error; treat it as a no-op rather than failing the install.
                    if (IndicatesVaultAlreadyInitialized(result))
                    {
                        session.SendMessage(
                            "MiniVault: Vault already initialized; skipping init (upgrade).",
                            InstallMessage.INFO);
                        return (int)ActionResult.Success;
                    }

                    session.SendMessage(
                        "MiniVault: " + FirstErrorLine(result), InstallMessage.ERROR);
                    return (int)ActionResult.Failure;
                }

                // The CLI writes the material to --out itself; keep stdout only as a fallback for a
                // build where it did not (an empty file would otherwise lose the material silently).
                if (!File.Exists(outFile) && result.StdOut.Trim().Length > 0)
                {
                    File.WriteAllText(outFile, result.StdOut, new UTF8Encoding(false));
                }

                session.SendMessage(
                    "MiniVault: the vault was initialized. The recovery material is in " + outFile +
                    " - copy it to a safe offline location and delete the file. It cannot be retrieved again.",
                    InstallMessage.INFO);

                return (int)ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.SendMessage("MiniVault: 'minivault.exe init' failed: " + ex.Message, InstallMessage.ERROR);
                return (int)ActionResult.Failure;
            }
        }

        // -------------------------------------------------------------------
        // TestSqlConnection (immediate)
        // -------------------------------------------------------------------

        /// <summary>
        /// Opens the connection string in MV_CONNECTIONSTRING with a 5 second timeout and reports the
        /// outcome in MV_SQL_OK (1 or 0) and MV_SQL_ERROR. Immediate, so it can back a "Test
        /// connection" button; it never fails the installation.
        /// </summary>
        public static int TestSqlConnection(string sessionHandle)
        {
            return TestSqlConnection(new MsiSession(sessionHandle));
        }

        internal static int TestSqlConnection(IMsiSession session)
        {
            try
            {
                var connectionString = session.GetProperty("MV_CONNECTIONSTRING");

                if (string.IsNullOrEmpty(connectionString) || connectionString.Trim().Length == 0)
                {
                    session.SetProperty(SqlOkProperty, "0");
                    session.SetProperty(SqlErrorProperty, "No connection string was entered.");
                    return (int)ActionResult.Success;
                }

                var builder = new SqlConnectionStringBuilder(connectionString)
                {
                    ConnectTimeout = SqlConnectTimeoutSeconds
                };

                using (var connection = new SqlConnection(builder.ConnectionString))
                {
                    connection.Open();
                }

                session.SetProperty(SqlOkProperty, "1");
                session.SetProperty(SqlErrorProperty, string.Empty);
                return (int)ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.SetProperty(SqlOkProperty, "0");
                session.SetProperty(SqlErrorProperty, ex.Message);
                return (int)ActionResult.Success;
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        internal static string ResolveProgramDataDir(InstallModel model)
        {
            if (model != null && !string.IsNullOrEmpty(model.ProgramDataDir) && model.ProgramDataDir.Trim().Length > 0)
            {
                return model.ProgramDataDir.TrimEnd('\\');
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiniVault");
        }

        /// <summary>
        /// Picks the CLI's own 'Error: ...' line out of the captured output so the MSI error box
        /// shows what the CLI complained about rather than a bare exit code.
        /// </summary>
        internal static string FirstErrorLine(ProcessResult result)
        {
            foreach (var stream in new[] { result.StdErr, result.StdOut })
            {
                foreach (var line in stream.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed;
                    }
                }
            }

            var fallback = result.StdErr.Trim();
            if (fallback.Length == 0)
            {
                fallback = result.StdOut.Trim();
            }

            return string.Format(CultureInfo.InvariantCulture,
                "'minivault.exe init' failed with exit code {0}. {1}", result.ExitCode, fallback).Trim();
        }

        /// <summary>
        /// True when the CLI's captured output indicates 'minivault.exe init' failed because the
        /// vault was already initialized (VaultAlreadyInitializedException's message), rather than
        /// some other error.
        /// </summary>
        internal static bool IndicatesVaultAlreadyInitialized(ProcessResult result)
        {
            return ContainsAlreadyInitialized(result.StdOut) || ContainsAlreadyInitialized(result.StdErr);
        }

        private static bool ContainsAlreadyInitialized(string text)
        {
            return text != null && text.IndexOf("already initialized", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NullIfBlank(string value)
        {
            return string.IsNullOrEmpty(value) || value.Trim().Length == 0 ? null : value.Trim();
        }
    }
}
