using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
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

        /// <summary>"1" to overwrite an existing %ProgramData%\MiniVault\appsettings.json. Empty (the default)
        /// keeps whatever is already there, which is what makes an upgrade non-destructive.</summary>
        [InstallArgument("MV_RECONFIGURE")]
        public string Reconfigure { get; set; }
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

                // An upgrade re-runs this action with whatever MV_* properties msiexec was given - which, for an
                // upgrade started from Add/Remove Programs, is the defaults. Overwriting a working configuration
                // with those would take the server down, so an existing file is kept unless MV_RECONFIGURE=1.
                // The ACL is still (re-)applied: it is idempotent, and it is what the installed service depends on.
                if (File.Exists(configPath) && !IsReconfigureRequested(model))
                {
                    DirectoryAcl.Protect(programDataDir, model.ServiceAccount);
                    session.SendMessage(
                        string.Format(CultureInfo.InvariantCulture,
                            "MiniVault: {0} already exists; existing configuration kept (pass MV_RECONFIGURE=1 to overwrite it). The ACL was re-applied ({1}).",
                            configPath, string.Join(" ", DirectoryAcl.DescribeGrants(model.ServiceAccount))),
                        InstallMessage.INFO);
                    return (int)ActionResult.Success;
                }

                // The connection string goes to disk DPAPI-protected (LocalMachine), never in clear text: a SQL
                // login's password would otherwise sit in appsettings.json guarded only by the folder ACL. The
                // action runs on the target machine, which is what binds the value to it.
                var config = new MachineConfig
                {
                    ConnectionString = model.ConnectionString,
                    ProtectedConnectionString = string.IsNullOrEmpty(model.ConnectionString) ? null : ConfigProtection.Protect(model.ConnectionString),
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

                // The master-key password goes to the child through the environment, never on its command line:
                // a deferred custom action's command line reaches the process list and the MSI verbose log.
                var result = processRunner.Run(
                    exePath, arguments, MiniVaultCli.BuildInitEnvironment(model.MasterKey));

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
        // ValidateProperties (immediate)
        // -------------------------------------------------------------------

        /// <summary>The properties whose values travel to the deferred actions inside CustomActionData.</summary>
        internal static readonly string[] QuoteSensitiveProperties =
        {
            "MV_CONNECTIONSTRING",
            "MV_CERT_PASSWORD",
            "MV_MASTERKEY",
            "MV_SERVICEACCOUNT_PASSWORD"
        };

        /// <summary>
        /// Fails the installation, with a message naming the property, when a value that has to survive
        /// CustomActionData contains a double quote. CustomActionData is a
        /// <c>NAME="value", NAME2="value2"</c> list, so an embedded <c>"</c> ends the value early and the deferred
        /// action silently receives a truncated connection string or password. Immediate and sequenced right after
        /// LaunchConditions, so this is caught before anything is installed.
        /// </summary>
        public static int ValidateProperties(string sessionHandle)
        {
            return ValidateProperties(new MsiSession(sessionHandle));
        }

        internal static int ValidateProperties(IMsiSession session)
        {
            try
            {
                foreach (var property in QuoteSensitiveProperties)
                {
                    var value = session.GetProperty(property);
                    if (!string.IsNullOrEmpty(value) && value.IndexOf('"') >= 0)
                    {
                        session.SendMessage(
                            string.Format(CultureInfo.InvariantCulture,
                                "MiniVault: {0} must not contain a double quote (\"). The installer passes it to its "
                                + "deferred actions as NAME=\"value\", so a quote would truncate the value. Choose a "
                                + "value without quotes.",
                                property),
                            InstallMessage.ERROR);
                        return (int)ActionResult.Failure;
                    }
                }

                // The recovery options are what RunInit hands to 'minivault.exe init'. Checking them here, with the
                // same rule MiniVaultCli applies, fails a bad MV_RECOVERY / MV_SHARES / MV_THRESHOLD before
                // InstallInitialize instead of halfway through the install. RecoveryDlg checks the ranges too, but
                // "threshold <= shares" needs a numeric comparison of two properties that MSI conditions cannot
                // express, and a silent install never sees the dialog at all.
                var recoveryError = DescribeRecoveryOptionsError(
                    session.GetProperty("MV_RECOVERY"), session.GetProperty("MV_SHARES"), session.GetProperty("MV_THRESHOLD"));
                if (recoveryError != null)
                {
                    session.SendMessage("MiniVault: " + recoveryError, InstallMessage.ERROR);
                    return (int)ActionResult.Failure;
                }

                return (int)ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.SendMessage("MiniVault: property validation failed: " + ex.Message, InstallMessage.ERROR);
                return (int)ActionResult.Failure;
            }
        }

        /// <summary>
        /// Null when MV_RECOVERY / MV_SHARES / MV_THRESHOLD would be accepted by 'minivault.exe init', otherwise the
        /// message to fail the installation with. Shares and threshold are only looked at for shamir; an empty
        /// recovery mode means single, exactly as <see cref="MiniVaultCli.BuildInitArguments"/> treats it.
        /// </summary>
        internal static string DescribeRecoveryOptionsError(string recovery, string shares, string threshold)
        {
            var mode = string.IsNullOrEmpty(recovery) ? "single" : recovery.Trim().ToLowerInvariant();
            if (mode != "single" && mode != "shamir")
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "MV_RECOVERY must be 'single' or 'shamir' (got '{0}').", recovery);
            }

            var sharesValue = 0;
            var thresholdValue = 0;
            if (mode == "shamir")
            {
                if (!int.TryParse((shares ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sharesValue)
                    || !int.TryParse((threshold ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out thresholdValue))
                {
                    return string.Format(CultureInfo.InvariantCulture,
                        "MV_SHARES and MV_THRESHOLD must be whole numbers for shamir recovery (got shares='{0}', threshold='{1}').",
                        shares, threshold);
                }
            }

            try
            {
                MiniVaultCli.BuildInitArguments(mode, sharesValue, thresholdValue, null, "validate-only");
                return null;
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        // -------------------------------------------------------------------
        // TestSqlConnection (immediate)
        // -------------------------------------------------------------------

        internal const string SqlNoteProperty = "MV_SQL_NOTE";

        /// <summary>SQL Server error 4060: the login is fine but "Cannot open database ... requested by the login".</summary>
        private const int SqlCannotOpenDatabase = 4060;

        /// <summary>
        /// Opens the connection string in MV_CONNECTIONSTRING with a 5 second timeout and reports the
        /// outcome in MV_SQL_OK (1 or 0), MV_SQL_ERROR and MV_SQL_NOTE. Immediate, so it can back a "Test
        /// connection" button; it never fails the installation.
        /// A database that does not exist yet is the normal first-install case - 'minivault init' creates
        /// it - so error 4060 is followed up against master: if the database really is absent and the login
        /// may create databases, the test passes with a note; if it exists but the login cannot open it, or
        /// the login cannot create it, the test fails with a message saying which.
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
                    // Say what the page actually asked for: the server name in the normal mode, the
                    // string itself in the advanced mode.
                    var message = IsTicked(session.GetProperty(SqlAdvancedProperty))
                        ? "Enter the connection string."
                        : "Enter the server name (for example sql01, sql01\\INSTANCE or 192.168.1.45,1433).";
                    ReportSql(session, false, message, null);
                    return (int)ActionResult.Success;
                }

                var builder = new SqlConnectionStringBuilder(connectionString)
                {
                    ConnectTimeout = SqlConnectTimeoutSeconds
                };

                try
                {
                    using (var connection = new SqlConnection(builder.ConnectionString))
                    {
                        connection.Open();
                    }

                    ReportSql(session, true, null, null);
                    return (int)ActionResult.Success;
                }
                catch (SqlException ex) when (ex.Number == SqlCannotOpenDatabase && !string.IsNullOrEmpty(builder.InitialCatalog))
                {
                    var databaseName = builder.InitialCatalog;
                    var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
                    using (var connection = new SqlConnection(master.ConnectionString))
                    {
                        connection.Open();
                        bool exists;
                        bool canCreate;
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END, "
                                + "CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1 OR IS_SRVROLEMEMBER('dbcreator') = 1 "
                                + "OR HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE') = 1 THEN 1 ELSE 0 END";
                            command.Parameters.AddWithValue("@name", databaseName);
                            using (var reader = command.ExecuteReader())
                            {
                                reader.Read();
                                exists = reader.GetInt32(0) == 1;
                                canCreate = reader.GetInt32(1) == 1;
                            }
                        }

                        if (exists)
                        {
                            ReportSql(session, false, string.Format(CultureInfo.InvariantCulture,
                                "The server accepted the login, but the database '{0}' exists and this login cannot open it. "
                                + "Grant the login access to the database (db_owner for the account that runs the installation), or use another login.",
                                databaseName), null);
                        }
                        else if (!canCreate)
                        {
                            ReportSql(session, false, string.Format(CultureInfo.InvariantCulture,
                                "The server accepted the login, but the database '{0}' does not exist and this login may not create databases. "
                                + "Create the database first, or give the login the dbcreator role for the installation.",
                                databaseName), null);
                        }
                        else
                        {
                            ReportSql(session, true, null, string.Format(CultureInfo.InvariantCulture,
                                "The database '{0}' does not exist yet; the installation creates it ('minivault init').",
                                databaseName));
                        }
                    }

                    return (int)ActionResult.Success;
                }
            }
            catch (Exception ex)
            {
                ReportSql(session, false, ex.Message, null);
                return (int)ActionResult.Success;
            }
        }

        private static void ReportSql(IMsiSession session, bool ok, string error, string note)
        {
            session.SetProperty(SqlOkProperty, ok ? "1" : "0");
            session.SetProperty(SqlErrorProperty, error ?? string.Empty);
            session.SetProperty(SqlNoteProperty, note ?? string.Empty);
            // One line for the message box the SqlDlg "Test connection" button spawns.
            session.SetProperty(SqlResultProperty, ok
                ? (string.IsNullOrEmpty(note) ? "Connection succeeded." : "Connection succeeded. " + note)
                : "Connection failed. " + (error ?? string.Empty));
        }

        internal const string SqlResultProperty = "MV_SQL_RESULT";

        // -------------------------------------------------------------------
        // BuildConnectionString (immediate)
        // -------------------------------------------------------------------

        internal const string SqlServerProperty = "MV_SQL_SERVER";
        internal const string SqlDatabaseProperty = "MV_SQL_DATABASE";
        internal const string SqlAuthProperty = "MV_SQL_AUTH";
        internal const string SqlUserProperty = "MV_SQL_USER";
        internal const string SqlPasswordProperty = "MV_SQL_PASSWORD";
        internal const string SqlEncryptProperty = "MV_SQL_ENCRYPT";
        internal const string SqlTrustCertProperty = "MV_SQL_TRUSTCERT";
        internal const string SqlAdvancedProperty = "MV_SQL_ADVANCED";
        internal const string ConnectionStringProperty = "MV_CONNECTIONSTRING";
        internal const string DefaultDatabaseName = "MiniVault";

        /// <summary>
        /// Composes MV_CONNECTIONSTRING from the SqlDlg fields (MV_SQL_SERVER, MV_SQL_DATABASE, MV_SQL_AUTH =
        /// windows|sql, MV_SQL_USER, MV_SQL_PASSWORD, MV_SQL_ENCRYPT, MV_SQL_TRUSTCERT) with
        /// <see cref="SqlConnectionStringBuilder"/>, so the operator never types connection-string syntax.
        /// Leaves MV_CONNECTIONSTRING alone when MV_SQL_ADVANCED is "1" (the operator typed the string) or when
        /// MV_SQL_SERVER is empty (a silent install that passed MV_CONNECTIONSTRING directly). Immediate; never
        /// fails the installation. Run by the SqlDlg buttons and, for silent installs that pass the parts
        /// instead of the string, once in the execute sequence before ValidateProperties.
        /// </summary>
        public static int BuildConnectionString(string sessionHandle)
        {
            return BuildConnectionString(new MsiSession(sessionHandle));
        }

        internal static int BuildConnectionString(IMsiSession session)
        {
            try
            {
                if (IsTicked(session.GetProperty(SqlAdvancedProperty)))
                {
                    return (int)ActionResult.Success;
                }

                var server = NullIfBlank(session.GetProperty(SqlServerProperty));
                if (server == null)
                {
                    return (int)ActionResult.Success;
                }

                // Composed by hand rather than with SqlConnectionStringBuilder.ConnectionString: that quotes a value
                // containing ';' with DOUBLE quotes, and a double quote anywhere in MV_CONNECTIONSTRING is exactly what
                // ValidateProperties has to reject (CustomActionData is a NAME="value" list). Single quotes, with an
                // embedded single quote doubled, are the other quoting SqlClient accepts and survive that list.
                var pairs = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Data Source", server),
                    new KeyValuePair<string, string>("Initial Catalog", NullIfBlank(session.GetProperty(SqlDatabaseProperty)) ?? DefaultDatabaseName)
                };

                var auth = (NullIfBlank(session.GetProperty(SqlAuthProperty)) ?? "windows").ToLowerInvariant();
                if (auth == "sql")
                {
                    pairs.Add(new KeyValuePair<string, string>("User ID", NullIfBlank(session.GetProperty(SqlUserProperty)) ?? string.Empty));
                    pairs.Add(new KeyValuePair<string, string>("Password", session.GetProperty(SqlPasswordProperty) ?? string.Empty));
                }
                else
                {
                    pairs.Add(new KeyValuePair<string, string>("Integrated Security", "True"));
                }

                pairs.Add(new KeyValuePair<string, string>("Encrypt", IsTicked(session.GetProperty(SqlEncryptProperty)) ? "True" : "False"));
                pairs.Add(new KeyValuePair<string, string>("TrustServerCertificate", IsTicked(session.GetProperty(SqlTrustCertProperty)) ? "True" : "False"));

                var composed = new StringBuilder();
                foreach (var pair in pairs)
                {
                    composed.Append(pair.Key).Append('=').Append(QuoteConnectionStringValue(pair.Value)).Append(';');
                }

                session.SetProperty(ConnectionStringProperty, composed.ToString());
                return (int)ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("MiniVault: could not compose the connection string: " + ex.Message, InstallMessage.INFO);
                return (int)ActionResult.Success;
            }
        }

        private static bool IsTicked(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Trim() == "1";
        }

        // -------------------------------------------------------------------
        // ShowUiMessage (immediate, dialogs only)
        // -------------------------------------------------------------------

        internal const string UiMessageProperty = "MV_UI_ERROR";
        internal const string UiMessageKindProperty = "MV_UI_KIND";
        internal const string UiMessageTitle = "Karmasis MiniVault Setup";

        /// <summary>
        /// Shows MV_UI_ERROR in a modal message box parented to the wizard window - the same pattern
        /// Karmasis.InfraskopeServer's setup uses for its "Verify" buttons. MV_UI_KIND "info" picks the
        /// information icon, anything else the warning icon. Dialog pages run it (after their own
        /// data setter) where a SpawnDialog would otherwise be used; Advanced Installer's UI engine drops a
        /// SpawnDialog published by a control that also refreshes the page, a MessageBox is not affected.
        /// Outside the full UI (UILevel other than 5) the message only goes to the log. Never fails.
        /// </summary>
        public static int ShowUiMessage(string sessionHandle)
        {
            var session = new MsiSession(sessionHandle);
            return ShowUiMessage(session, (text, info) =>
            {
                if (session.GetProperty("UILevel") != "5")
                {
                    session.Log("MiniVault: " + text, InstallMessage.INFO);
                    return;
                }

                MessageBox.Show(
                    new Win32Window(session.GetMsiWindowHandle()),
                    text,
                    UiMessageTitle,
                    MessageBoxButtons.OK,
                    info ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            });
        }

        internal static int ShowUiMessage(IMsiSession session, Action<string, bool> show)
        {
            try
            {
                var text = session.GetProperty(UiMessageProperty);
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
                {
                    return (int)ActionResult.Success;
                }

                var kind = session.GetProperty(UiMessageKindProperty) ?? string.Empty;
                show(text, kind.Trim().Equals("info", StringComparison.OrdinalIgnoreCase));
                return (int)ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("MiniVault: could not show the message: " + ex.Message, InstallMessage.INFO);
                return (int)ActionResult.Success;
            }
        }

        /// <summary>
        /// Quotes a connection-string value the way SqlClient reads it back: plain when it contains nothing
        /// special, otherwise in single quotes with an embedded single quote doubled. Never emits a double quote.
        /// </summary>
        internal static string QuoteConnectionStringValue(string value)
        {
            value = value ?? string.Empty;
            var needsQuotes = value.Length == 0
                || value.IndexOfAny(new[] { ';', '\'', '"', '=' }) >= 0
                || value[0] == ' ' || value[value.Length - 1] == ' ';
            return needsQuotes ? "'" + value.Replace("'", "''") + "'" : value;
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

        /// <summary>True when MV_RECONFIGURE asks for an existing appsettings.json to be overwritten.</summary>
        internal static bool IsReconfigureRequested(InstallModel model)
        {
            return model != null && !string.IsNullOrEmpty(model.Reconfigure) && model.Reconfigure.Trim() == "1";
        }

        private static string NullIfBlank(string value)
        {
            return string.IsNullOrEmpty(value) || value.Trim().Length == 0 ? null : value.Trim();
        }
    }
}
