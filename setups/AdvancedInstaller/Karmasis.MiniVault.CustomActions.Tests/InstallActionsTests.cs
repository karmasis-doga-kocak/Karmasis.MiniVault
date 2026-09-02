using System;
using System.Collections.Generic;
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

        [Fact]
        public void RunInit_WithAMasterKeyPassword_PassesMasterKey()
        {
            using (var directory = new TempDirectory(create: false))
            {
                var session = new FakeMsiSession(CustomActionData(
                    directory.Path, @"C:\MiniVault", ", MV_RECOVERY=\"single\", MV_MASTERKEY=\"pa ss\""));
                var runner = new FakeProcessRunner(0, "Recovery key: abc\n");

                InstallActions.RunInit(session, runner).ShouldBe((int)ActionResult.Success);

                var arguments = runner.LastArguments;
                arguments[Array.IndexOf(arguments, "--master-key") + 1].ShouldBe("pa ss");
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
