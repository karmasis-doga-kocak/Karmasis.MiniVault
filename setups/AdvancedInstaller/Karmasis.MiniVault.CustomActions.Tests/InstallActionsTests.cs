using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Karmasis.AdvancedInstallerKit;
using Shouldly;
using Xunit;

namespace Karmasis.MiniVault.CustomActions.Tests
{
    public class InstallActionsTests
    {
        private static string CustomActionData(string programDataDir, string appDir, string extra)
        {
            return string.Format(
                "MV_CONNECTIONSTRING=\"Server=sql01;Database=MiniVault;Integrated Security=true\", " +
                "MV_SERVICEACCOUNT=\"LocalSystem\", MV_URL=\"https://0.0.0.0:8200\", " +
                "MV_PROGRAMDATA=\"{0}\", APPDIR=\"{1}\"{2}",
                programDataDir, appDir, extra);
        }

        // -------------------------------------------------------------------
        // WriteMachineConfig
        // -------------------------------------------------------------------

        [Fact]
        public void WriteMachineConfig_WritesAppSettingsIntoTheProgramDataFolder()
        {
            using (var directory = new TempDirectory())
            {
                // The service account is granted read access, so run the test as its own service
                // account: with the production default (LocalSystem) the protected ACL would lock
                // this non-elevated test process out of the file it just asked for.
                var session = new FakeMsiSession(string.Format(
                    "MV_CONNECTIONSTRING=\"Server=sql01;Database=MiniVault;Integrated Security=true\", " +
                    "MV_SERVICEACCOUNT=\"{0}\", MV_URL=\"https://0.0.0.0:8200\", MV_PROGRAMDATA=\"{1}\", " +
                    "MV_CERT_THUMBPRINT=\"0123456789ABCDEF0123456789ABCDEF01234567\"",
                    WindowsIdentity.GetCurrent().Name, directory.Path));

                InstallActions.WriteMachineConfig(session).ShouldBe((int)ActionResult.Success);

                var configPath = directory.File("appsettings.json");
                File.Exists(configPath).ShouldBeTrue();

                var json = File.ReadAllText(configPath);
                json.ShouldContain("\"MiniVault\": \"Server=sql01;Database=MiniVault;Integrated Security=true\"");
                json.ShouldContain("\"Provider\": \"Dpapi\"");
                json.ShouldContain("\"Thumbprint\": \"0123456789ABCDEF0123456789ABCDEF01234567\"");
                json.ShouldContain("\"Url\": \"https://0.0.0.0:8200\"");

                session.HasMessage(InstallMessage.ERROR).ShouldBeFalse();
            }
        }

        [Fact]
        public void WriteMachineConfig_ProtectsTheProgramDataFolder()
        {
            using (var directory = new TempDirectory())
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\Program Files\Karmasis\MiniVault",
                    ", MV_CERT_THUMBPRINT=\"0123456789ABCDEF0123456789ABCDEF01234567\""));

                InstallActions.WriteMachineConfig(session).ShouldBe((int)ActionResult.Success);

                // Inheritance is off and only SYSTEM and Administrators remain, so this
                // (non-elevated) process can read the DACL as owner but not the folder itself.
                var security = new DirectoryInfo(directory.Path).GetAccessControl();
                security.AreAccessRulesProtected.ShouldBeTrue();

                var sids = new List<string>();
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    sids.Add(rule.IdentityReference.Value);
                }

                sids.ShouldContain("S-1-5-18");      // NT AUTHORITY\SYSTEM
                sids.ShouldContain("S-1-5-32-544");  // BUILTIN\Administrators
                sids.Count.ShouldBe(2);              // LocalSystem service account adds nothing more
            }
        }

        [Fact]
        public void WriteMachineConfig_WithoutACertificate_FailsWithAnErrorMessage()
        {
            using (var directory = new TempDirectory())
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\Program Files\Karmasis\MiniVault", string.Empty));

                InstallActions.WriteMachineConfig(session).ShouldBe((int)ActionResult.Failure);

                session.LastMessage(InstallMessage.ERROR).ShouldContain("MV_CERT_PATH");
                File.Exists(directory.File("appsettings.json")).ShouldBeFalse();
            }
        }

        /// <summary>An upgrade re-runs WriteMachineConfig with whatever properties msiexec was given - the
        /// defaults, when it was started from Add/Remove Programs. The existing configuration must survive that.
        /// </summary>
        [Fact]
        public void WriteMachineConfig_WhenAppSettingsAlreadyExists_KeepsItAndStillAppliesTheAcl()
        {
            using (var directory = new TempDirectory())
            {
                var configPath = directory.File("appsettings.json");
                File.WriteAllText(configPath, "{ \"kept\": true }");

                // Runs as the current account, because the kept file is read back through the protected ACL.
                var session = new FakeMsiSession(string.Format(
                    "MV_CONNECTIONSTRING=\"Server=sql01;Database=MiniVault;Integrated Security=true\", " +
                    "MV_SERVICEACCOUNT=\"{0}\", MV_URL=\"https://0.0.0.0:8200\", MV_PROGRAMDATA=\"{1}\", " +
                    "MV_CERT_THUMBPRINT=\"0123456789ABCDEF0123456789ABCDEF01234567\"",
                    WindowsIdentity.GetCurrent().Name, directory.Path));

                InstallActions.WriteMachineConfig(session).ShouldBe((int)ActionResult.Success);

                File.ReadAllText(configPath).ShouldBe("{ \"kept\": true }");
                session.HasMessage(InstallMessage.ERROR).ShouldBeFalse();
                session.LastMessage(InstallMessage.INFO).ShouldContain("existing configuration kept");
                new DirectoryInfo(directory.Path).GetAccessControl().AreAccessRulesProtected.ShouldBeTrue();
            }
        }

        [Fact]
        public void WriteMachineConfig_WithReconfigure_OverwritesAnExistingAppSettings()
        {
            using (var directory = new TempDirectory())
            {
                var configPath = directory.File("appsettings.json");
                File.WriteAllText(configPath, "{ \"kept\": true }");

                // Runs as the current account, because the rewritten file is read back below.
                var session = new FakeMsiSession(string.Format(
                    "MV_CONNECTIONSTRING=\"Server=sql01;Database=MiniVault;Integrated Security=true\", " +
                    "MV_SERVICEACCOUNT=\"{0}\", MV_URL=\"https://0.0.0.0:8200\", MV_PROGRAMDATA=\"{1}\", " +
                    "MV_CERT_THUMBPRINT=\"0123456789ABCDEF0123456789ABCDEF01234567\", MV_RECONFIGURE=\"1\"",
                    WindowsIdentity.GetCurrent().Name, directory.Path));

                InstallActions.WriteMachineConfig(session).ShouldBe((int)ActionResult.Success);

                File.ReadAllText(configPath).ShouldContain("\"Url\": \"https://0.0.0.0:8200\"");
                session.LastMessage(InstallMessage.INFO).ShouldContain("wrote ");
            }
        }

        // -------------------------------------------------------------------
        // ValidateProperties
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("MV_CONNECTIONSTRING")]
        [InlineData("MV_CERT_PASSWORD")]
        [InlineData("MV_MASTERKEY")]
        [InlineData("MV_SERVICEACCOUNT_PASSWORD")]
        public void ValidateProperties_WithADoubleQuote_FailsAndNamesTheProperty(string property)
        {
            var session = new FakeMsiSession();
            session.SetProperty(property, "va\"lue");

            InstallActions.ValidateProperties(session).ShouldBe((int)ActionResult.Failure);

            session.LastMessage(InstallMessage.ERROR).ShouldContain(property);
        }

        [Fact]
        public void ValidateProperties_WithQuoteFreeValues_Succeeds()
        {
            var session = new FakeMsiSession();
            session.SetProperty("MV_CONNECTIONSTRING", "Server=sql01;Database=MiniVault;Integrated Security=true");
            session.SetProperty("MV_CERT_PASSWORD", "p@ss w0rd!");
            session.SetProperty("MV_MASTERKEY", "another one");
            session.SetProperty("MV_SERVICEACCOUNT_PASSWORD", "svc-p@ss");

            InstallActions.ValidateProperties(session).ShouldBe((int)ActionResult.Success);

            session.HasMessage(InstallMessage.ERROR).ShouldBeFalse();
        }

        [Fact]
        public void ValidateProperties_WithNothingSet_Succeeds()
        {
            var session = new FakeMsiSession();

            InstallActions.ValidateProperties(session).ShouldBe((int)ActionResult.Success);
        }

        [Theory]
        [InlineData("single", "", "")]
        [InlineData("", "", "")]
        [InlineData("single", "1", "9")]   // ignored for single
        [InlineData("shamir", "3", "2")]
        [InlineData("Shamir", " 5 ", " 5 ")]
        [InlineData("shamir", "255", "2")]
        public void ValidateProperties_WithValidRecoveryOptions_Succeeds(string recovery, string shares, string threshold)
        {
            var session = new FakeMsiSession();
            session.SetProperty("MV_RECOVERY", recovery);
            session.SetProperty("MV_SHARES", shares);
            session.SetProperty("MV_THRESHOLD", threshold);

            InstallActions.ValidateProperties(session).ShouldBe((int)ActionResult.Success);

            session.HasMessage(InstallMessage.ERROR).ShouldBeFalse();
        }

        [Theory]
        [InlineData("other", "3", "2", "MV_RECOVERY")]
        [InlineData("shamir", "", "2", "whole numbers")]
        [InlineData("shamir", "three", "2", "whole numbers")]
        [InlineData("shamir", "3", "", "whole numbers")]
        [InlineData("shamir", "1", "1", "threshold <= shares <= 255")]
        [InlineData("shamir", "3", "1", "threshold <= shares <= 255")]
        [InlineData("shamir", "2", "3", "threshold <= shares <= 255")]   // the rule the dialog cannot check
        [InlineData("shamir", "256", "2", "threshold <= shares <= 255")]
        public void ValidateProperties_WithInvalidRecoveryOptions_FailsBeforeInstallInitialize(string recovery, string shares, string threshold, string expectedFragment)
        {
            var session = new FakeMsiSession();
            session.SetProperty("MV_RECOVERY", recovery);
            session.SetProperty("MV_SHARES", shares);
            session.SetProperty("MV_THRESHOLD", threshold);

            InstallActions.ValidateProperties(session).ShouldBe((int)ActionResult.Failure);

            session.LastMessage(InstallMessage.ERROR).ShouldContain(expectedFragment);
        }

        // -------------------------------------------------------------------
        // RunInit
        // -------------------------------------------------------------------

        [Fact]
        public void RunInit_Single_RunsMinivaultExeFromAppDirWithAnOutFileInProgramData()
        {
            using (var directory = new TempDirectory(create: false))
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\Program Files\Karmasis\MiniVault", ", MV_RECOVERY=\"single\""));
                var runner = new FakeProcessRunner(0, "Recovery key: abc\n");

                InstallActions.RunInit(session, runner).ShouldBe((int)ActionResult.Success);

                runner.Invocations.ShouldBe(1);
                runner.LastExePath.ShouldBe(@"C:\Program Files\Karmasis\MiniVault\minivault.exe");
                runner.LastArguments[0].ShouldBe("init");
                runner.LastArguments.ShouldContain("--recovery");
                runner.LastArguments.ShouldContain("single");
                runner.LastArguments.ShouldNotContain("--shares");
                runner.LastArguments.ShouldNotContain("--master-key");

                var outFile = runner.LastArguments[Array.IndexOf(runner.LastArguments, "--out") + 1];
                Path.GetDirectoryName(outFile).ShouldBe(directory.Path);
                Path.GetFileName(outFile).ShouldStartWith("recovery-");
                Path.GetExtension(outFile).ShouldBe(".txt");

                // The CLI did not write the file (fake runner), so stdout is preserved as a fallback.
                File.Exists(outFile).ShouldBeTrue();
                File.ReadAllText(outFile).ShouldContain("Recovery key: abc");

                // Deferred custom actions cannot set properties, so the operator is pointed at the file.
                session.LastMessage(InstallMessage.INFO).ShouldContain(outFile);
                session.Properties.ShouldNotContainKey("MV_RECOVERY_OUTPUT");
            }
        }

        [Fact]
        public void RunInit_Shamir_PassesSharesAndThreshold()
        {
            using (var directory = new TempDirectory(create: false))
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\MiniVault",
                    ", MV_RECOVERY=\"shamir\", MV_SHARES=\"5\", MV_THRESHOLD=\"3\""));
                var runner = new FakeProcessRunner(0, "Share 1: a\nShare 2: b\n");

                InstallActions.RunInit(session, runner).ShouldBe((int)ActionResult.Success);

                var arguments = runner.LastArguments;
                arguments[Array.IndexOf(arguments, "--recovery") + 1].ShouldBe("shamir");
                arguments[Array.IndexOf(arguments, "--shares") + 1].ShouldBe("5");
                arguments[Array.IndexOf(arguments, "--threshold") + 1].ShouldBe("3");
            }
        }

        /// <summary>A deferred custom action's command line is visible in the process list and in the MSI verbose
        /// log, so the master-key password goes to minivault.exe through the environment instead.</summary>
        [Fact]
        public void RunInit_WithAMasterKeyPassword_PassesItThroughTheEnvironment_NotTheCommandLine()
        {
            using (var directory = new TempDirectory(create: false))
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\MiniVault", ", MV_RECOVERY=\"single\", MV_MASTERKEY=\"pa ss\""));
                var runner = new FakeProcessRunner(0, "Recovery key: abc\n");

                InstallActions.RunInit(session, runner).ShouldBe((int)ActionResult.Success);

                runner.LastArguments.ShouldContain("--master-key-from-env");
                runner.LastArguments.ShouldNotContain("--master-key");
                runner.LastArguments.ShouldNotContain("pa ss");
                runner.LastEnvironment.ShouldNotBeNull();
                runner.LastEnvironment[MiniVaultCli.MasterKeyEnvironmentVariable].ShouldBe("pa ss");
            }
        }

        [Fact]
        public void RunInit_WithoutAMasterKeyPassword_PassesNoExtraEnvironment()
        {
            using (var directory = new TempDirectory(create: false))
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\MiniVault", ", MV_RECOVERY=\"single\""));
                var runner = new FakeProcessRunner(0, "Recovery key: abc\n");

                InstallActions.RunInit(session, runner).ShouldBe((int)ActionResult.Success);

                runner.LastArguments.ShouldNotContain("--master-key-from-env");
                runner.LastEnvironment.ShouldBeNull();
            }
        }

        [Fact]
        public void RunInit_WhenTheCliFails_ReportsItsErrorLineAndFails()
        {
            using (var directory = new TempDirectory(create: false))
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\MiniVault", ", MV_RECOVERY=\"single\""));
                var runner = new FakeProcessRunner(1, string.Empty, "Error: could not open the vault database.\n");

                InstallActions.RunInit(session, runner).ShouldBe((int)ActionResult.Failure);

                session.LastMessage(InstallMessage.ERROR)
                    .ShouldContain("Error: could not open the vault database.");
            }
        }

        [Fact]
        public void RunInit_WhenTheVaultIsAlreadyInitialized_TreatsInitAsANoOpOnUpgradeAndSucceeds()
        {
            using (var directory = new TempDirectory(create: false))
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\MiniVault", ", MV_RECOVERY=\"single\""));
                var runner = new FakeProcessRunner(1, string.Empty,
                    "Error: The vault is already initialized. Use 'minivault recover' to change the master key.\n");

                InstallActions.RunInit(session, runner).ShouldBe((int)ActionResult.Success);

                session.HasMessage(InstallMessage.ERROR).ShouldBeFalse();
                session.LastMessage(InstallMessage.INFO).ShouldContain("already initialized");
            }
        }

        [Fact]
        public void RunInit_WithoutAppDir_FailsBeforeStartingAnything()
        {
            var session = new FakeMsiSession("MV_RECOVERY=\"single\"");
            var runner = new FakeProcessRunner();

            InstallActions.RunInit(session, runner).ShouldBe((int)ActionResult.Failure);

            runner.Invocations.ShouldBe(0);
            session.LastMessage(InstallMessage.ERROR).ShouldContain("APPDIR");
        }

        // -------------------------------------------------------------------
        // TestSqlConnection
        // -------------------------------------------------------------------

        [Fact]
        public void TestSqlConnection_WithAnUnreachableServer_SetsSqlOkToZeroAndAnError()
        {
            var session = new FakeMsiSession();
            // Nothing listens on this port, so the attempt fails inside the 5 second timeout.
            session.SetProperty("MV_CONNECTIONSTRING", "Server=127.0.0.1,14339;Database=MiniVault;Integrated Security=true");

            InstallActions.TestSqlConnection(session).ShouldBe((int)ActionResult.Success);

            session.GetProperty(InstallActions.SqlOkProperty).ShouldBe("0");
            session.GetProperty(InstallActions.SqlErrorProperty).ShouldNotBeNullOrWhiteSpace();
        }

        [Fact]
        public void TestSqlConnection_WithoutAConnectionString_SetsSqlOkToZero()
        {
            var session = new FakeMsiSession();

            InstallActions.TestSqlConnection(session).ShouldBe((int)ActionResult.Success);

            session.GetProperty(InstallActions.SqlOkProperty).ShouldBe("0");
            session.GetProperty(InstallActions.SqlErrorProperty).ShouldContain("No connection string");
        }

        [Fact]
        public void TestSqlConnection_WithAnUnparseableConnectionString_SetsSqlOkToZero()
        {
            var session = new FakeMsiSession();
            session.SetProperty("MV_CONNECTIONSTRING", "this is not a connection string");

            InstallActions.TestSqlConnection(session).ShouldBe((int)ActionResult.Success);

            session.GetProperty(InstallActions.SqlOkProperty).ShouldBe("0");
            session.GetProperty(InstallActions.SqlErrorProperty).ShouldNotBeNullOrWhiteSpace();
        }

        // The next two need SQL Server LocalDB, like the server's integration tests. The test user is
        // sysadmin on its own LocalDB instance, so a missing database is the "init will create it" case.
        // The action connects with a 5 second timeout - fine against a running server, too short for a
        // cold LocalDB instance that first has to start - so the tests start the instance up front.
        private static void WarmUpLocalDb()
        {
            using (var connection = new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true;Connect Timeout=90"))
            {
                connection.Open();
            }
        }

        [Fact]
        public void TestSqlConnection_WhenTheDatabaseDoesNotExistYet_PassesWithANote()
        {
            WarmUpLocalDb();
            var session = new FakeMsiSession();
            session.SetProperty("MV_CONNECTIONSTRING",
                "Server=(localdb)\\MSSQLLocalDB;Database=MiniVaultSetupTest_DoesNotExist_" + Guid.NewGuid().ToString("N") + ";Integrated Security=true");

            InstallActions.TestSqlConnection(session).ShouldBe((int)ActionResult.Success);

            session.GetProperty(InstallActions.SqlOkProperty).ShouldBe("1");
            session.GetProperty(InstallActions.SqlErrorProperty).ShouldBeEmpty();
            session.GetProperty(InstallActions.SqlNoteProperty).ShouldContain("does not exist yet");
        }

        [Fact]
        public void TestSqlConnection_WhenTheDatabaseExists_PassesWithoutANote()
        {
            WarmUpLocalDb();
            var session = new FakeMsiSession();
            session.SetProperty("MV_CONNECTIONSTRING", "Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true");

            InstallActions.TestSqlConnection(session).ShouldBe((int)ActionResult.Success);

            session.GetProperty(InstallActions.SqlOkProperty).ShouldBe("1");
            session.GetProperty(InstallActions.SqlErrorProperty).ShouldBeEmpty();
            session.GetProperty(InstallActions.SqlNoteProperty).ShouldBeEmpty();
            session.GetProperty(InstallActions.SqlResultProperty).ShouldBe("Connection succeeded.");
        }

        // -------------------------------------------------------------------
        // BuildConnectionString
        // -------------------------------------------------------------------

        [Fact]
        public void BuildConnectionString_WindowsAuthentication_ComposesAnIntegratedSecurityString()
        {
            var session = new FakeMsiSession();
            session.SetProperty(InstallActions.SqlServerProperty, " sql01\\INST,1433 ");
            session.SetProperty(InstallActions.SqlDatabaseProperty, "MiniVaultProd");
            session.SetProperty(InstallActions.SqlAuthProperty, "windows");
            session.SetProperty(InstallActions.SqlEncryptProperty, "1");

            InstallActions.BuildConnectionString(session).ShouldBe((int)ActionResult.Success);

            var built = new SqlConnectionStringBuilder(session.GetProperty(InstallActions.ConnectionStringProperty));
            built.DataSource.ShouldBe("sql01\\INST,1433");
            built.InitialCatalog.ShouldBe("MiniVaultProd");
            built.IntegratedSecurity.ShouldBeTrue();
            built.Encrypt.ShouldBeTrue();
            built.TrustServerCertificate.ShouldBeFalse();
            built.UserID.ShouldBeEmpty();
        }

        [Fact]
        public void BuildConnectionString_SqlAuthentication_UsesLoginAndPasswordAndDefaultsTheDatabase()
        {
            var session = new FakeMsiSession();
            session.SetProperty(InstallActions.SqlServerProperty, "192.168.1.45");
            session.SetProperty(InstallActions.SqlAuthProperty, "sql");
            session.SetProperty(InstallActions.SqlUserProperty, "minivault_setup");
            session.SetProperty(InstallActions.SqlPasswordProperty, "p@ss;w0rd");
            session.SetProperty(InstallActions.SqlTrustCertProperty, "1");

            InstallActions.BuildConnectionString(session).ShouldBe((int)ActionResult.Success);

            var built = new SqlConnectionStringBuilder(session.GetProperty(InstallActions.ConnectionStringProperty));
            built.DataSource.ShouldBe("192.168.1.45");
            built.InitialCatalog.ShouldBe(InstallActions.DefaultDatabaseName);
            built.IntegratedSecurity.ShouldBeFalse();
            built.UserID.ShouldBe("minivault_setup");
            built.Password.ShouldBe("p@ss;w0rd");
            built.Encrypt.ShouldBeFalse();
            built.TrustServerCertificate.ShouldBeTrue();
            // The builder quotes the ';' with single quotes, so ValidateProperties' double-quote rule still holds.
            session.GetProperty(InstallActions.ConnectionStringProperty).ShouldNotContain("\"");
        }

        [Fact]
        public void BuildConnectionString_WhenTheAdvancedBoxIsTicked_LeavesTheTypedStringAlone()
        {
            var session = new FakeMsiSession();
            session.SetProperty(InstallActions.SqlAdvancedProperty, "1");
            session.SetProperty(InstallActions.SqlServerProperty, "ignored");
            session.SetProperty(InstallActions.ConnectionStringProperty, "Server=typed;Database=X;Integrated Security=true");

            InstallActions.BuildConnectionString(session).ShouldBe((int)ActionResult.Success);

            session.GetProperty(InstallActions.ConnectionStringProperty).ShouldBe("Server=typed;Database=X;Integrated Security=true");
        }

        // -------------------------------------------------------------------
        // ShowUiMessage
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("info", true)]
        [InlineData("INFO", true)]
        [InlineData("warn", false)]
        [InlineData("", false)]
        public void ShowUiMessage_ShowsMvUiErrorWithTheKindAsIcon(string kind, bool expectInfo)
        {
            var session = new FakeMsiSession();
            session.SetProperty(InstallActions.UiMessageProperty, "Connection succeeded.");
            session.SetProperty(InstallActions.UiMessageKindProperty, kind);
            string shownText = null; bool? shownInfo = null;

            InstallActions.ShowUiMessage(session, (text, info) => { shownText = text; shownInfo = info; }).ShouldBe((int)ActionResult.Success);

            shownText.ShouldBe("Connection succeeded.");
            shownInfo.ShouldBe(expectInfo);
        }

        [Fact]
        public void ShowUiMessage_WithNothingToShow_DoesNotOpenABox()
        {
            var session = new FakeMsiSession();
            var shown = false;

            InstallActions.ShowUiMessage(session, (text, info) => shown = true).ShouldBe((int)ActionResult.Success);

            shown.ShouldBeFalse();
        }

        [Fact]
        public void ShowUiMessage_WhenTheBoxThrows_LogsAndSucceeds()
        {
            var session = new FakeMsiSession();
            session.SetProperty(InstallActions.UiMessageProperty, "boom");

            InstallActions.ShowUiMessage(session, (text, info) => { throw new InvalidOperationException("no desktop"); }).ShouldBe((int)ActionResult.Success);

            session.HasMessage(InstallMessage.ERROR).ShouldBeFalse();
        }

        [Fact]
        public void BuildConnectionString_WithoutAServer_LeavesASilentInstallsConnectionStringAlone()
        {
            var session = new FakeMsiSession();
            session.SetProperty(InstallActions.ConnectionStringProperty, "Server=passed-on-the-command-line;Database=MiniVault;Integrated Security=true");

            InstallActions.BuildConnectionString(session).ShouldBe((int)ActionResult.Success);

            session.GetProperty(InstallActions.ConnectionStringProperty).ShouldBe("Server=passed-on-the-command-line;Database=MiniVault;Integrated Security=true");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        [Fact]
        public void ResolveProgramDataDir_FallsBackToCommonApplicationData()
        {
            InstallActions.ResolveProgramDataDir(new InstallModel())
                .ShouldBe(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiniVault"));
        }

        [Fact]
        public void DirectoryAcl_DescribeGrants_MatchesTheIcaclsGrantsInInstallPs1()
        {
            DirectoryAcl.DescribeGrants("LocalSystem")
                .ShouldBe(new[] { "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" });

            DirectoryAcl.DescribeGrants("NetworkService")
                .ShouldBe(new[] { "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-20:(OI)(CI)RX" });

            DirectoryAcl.DescribeGrants(@"CONTOSO\svc-minivault")
                .ShouldBe(new[] { "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", @"CONTOSO\svc-minivault:(OI)(CI)RX" });
        }
    }
}
