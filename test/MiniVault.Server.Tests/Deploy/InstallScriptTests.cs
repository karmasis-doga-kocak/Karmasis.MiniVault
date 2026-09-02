using System.Diagnostics;

namespace MiniVault.Server.Tests.Deploy;

/// <summary>
/// Exercises deploy/windows/install.ps1's -WhatIfMode and parameter validation by actually invoking
/// powershell.exe. Pester is not available in this repo, so this drives the script as a plain process
/// and asserts on its exit code and printed output.
/// </summary>
public class InstallScriptTests
{
    private static readonly string ScriptPath = FindInstallScript();

    [Fact]
    public void WhatIfMode_PrintsAllSixSteps_AndServiceName_AndExitsZero()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldBe(0, result.CombinedOutput);
        for (var step = 1; step <= 6; step++)
            result.CombinedOutput.ShouldContain($"Step {step}");
        result.CombinedOutput.ShouldContain("KarmasisMiniVault");
    }

    [Fact]
    public void SkipInit_ShowsStep4AsSkipped()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-SkipInit",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldBe(0, result.CombinedOutput);
        result.CombinedOutput.ShouldContain("Step 4: Skipped");
        result.CombinedOutput.ShouldContain("recover");
    }

    [Fact]
    public void MissingConnectionString_FailsWithNonZeroExit_AndMentionsConnectionString()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldNotBe(0);
        result.CombinedOutput.ShouldContain("ConnectionString");
    }

    [Fact]
    public void BothCertificateOptions_FailsWithNonZeroExit()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-CertificatePath", @"C:\certs\minivault.pfx",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public void ServiceAccount_IsGrantedByWellKnownSid_NotLocalizedName()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-ServiceAccount", @"NT AUTHORITY\NetworkService",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldBe(0, result.CombinedOutput);
        result.CombinedOutput.ShouldContain("S-1-5-18");        // SYSTEM
        result.CombinedOutput.ShouldContain("S-1-5-32-544");    // BUILTIN\Administrators
        result.CombinedOutput.ShouldContain("S-1-5-20");        // NETWORK SERVICE, granted read/execute
    }

    [Fact]
    public void ShortCertificateThumbprint_FailsWithNonZeroExit_AndMentionsThumbprint()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF0123456", // 39 characters
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldNotBe(0);
        result.CombinedOutput.ShouldContain("Thumbprint");
    }

    [Fact]
    public void CustomServiceAccountWithoutPassword_FailsWithNonZeroExit_AndMentionsServiceAccountPassword()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-ServiceAccount", @"CORP\svc-minivault",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldNotBe(0);
        result.CombinedOutput.ShouldContain("ServiceAccountPassword");
    }

    /// <summary>Any value carrying a double quote is rejected before anything is installed: it cannot
    /// survive the re-quoting on its way to sc.exe or to the MSI's CustomActionData.</summary>
    [Fact]
    public void CertificatePasswordWithADoubleQuote_FailsWithNonZeroExit_AndMentionsTheQuote()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificatePath", @"C:\certs\minivault.pfx",
            "-CertificatePassword", "pa\"ss",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldNotBe(0);
        result.CombinedOutput.ShouldContain("-CertificatePassword");
        result.CombinedOutput.ShouldContain("\"");
    }

    /// <summary>Two bad arguments must both be reported. Write-Error is terminating under
    /// $ErrorActionPreference = 'Stop', so one record per problem would print only the first.</summary>
    [Fact]
    public void TwoValidationErrors_AreBothPrinted()
    {
        if (!OperatingSystem.IsWindows()) return;

        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        // No -ConnectionString and no -SourceDir.
        var result = RunScript(
            "-WhatIfMode",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-InstallDir", installDir);

        result.ExitCode.ShouldNotBe(0);
        result.CombinedOutput.ShouldContain("-ConnectionString is required.");
        result.CombinedOutput.ShouldContain("-SourceDir is required");
    }

    /// <summary>The plan has to say what a failed health check will do, because it changes the exit
    /// code an automated caller sees.</summary>
    [Fact]
    public void WhatIfMode_MentionsIgnoreHealthCheck()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldBe(0, result.CombinedOutput);
        result.CombinedOutput.ShouldContain("-IgnoreHealthCheck");
        result.CombinedOutput.ShouldContain("exits 2");
    }

    /// <summary>A service account that is not built in needs "Log on as a service", or the SCM refuses
    /// to start the service with error 1069. The plan says so; the grant itself needs elevation, which
    /// this test does not have.</summary>
    [Fact]
    public void WhatIfMode_WithACustomServiceAccount_MentionsTheLogonRightGrant()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-ServiceAccount", @"CORP\svc-minivault",
            "-ServiceAccountPassword", "not-a-real-password",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldBe(0, result.CombinedOutput);
        result.CombinedOutput.ShouldContain("SeServiceLogonRight");
        result.CombinedOutput.ShouldContain("-SkipLogonRightGrant");
    }

    /// <summary>Least privilege: the running service reads and writes rows, it never changes the
    /// schema. DDL rights belong to the operator who runs init/migrate.</summary>
    [Fact]
    public void WhatIfMode_PrintsLeastPrivilegeSqlGrants()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "minivault-src-" + Guid.NewGuid());
        var installDir = Path.Combine(Path.GetTempPath(), "minivault-install-" + Guid.NewGuid());

        var result = RunScript(
            "-WhatIfMode",
            "-ConnectionString", "Server=x;Database=y;Integrated Security=true",
            "-CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567",
            "-SourceDir", sourceDir,
            "-InstallDir", installDir);

        result.ExitCode.ShouldBe(0, result.CombinedOutput);
        result.CombinedOutput.ShouldContain("db_datareader");
        result.CombinedOutput.ShouldContain("db_datawriter");
        result.CombinedOutput.ShouldContain("db_ddladmin");
        result.CombinedOutput.ShouldNotContain("ALTER ROLE db_owner ADD MEMBER [NT AUTHORITY\\SYSTEM]");
    }

    // Not covered here: the "service exists -> stop/config" wording and the SeServiceLogonRight grant
    // itself. Both need an actually-installed service, which needs elevation, and these tests run
    // unelevated in CI.

    private static (int ExitCode, string CombinedOutput) RunScript(params string[] scriptArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath);
        foreach (var arg in scriptArgs) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static string FindInstallScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Karmasis.MiniVault.sln").Length > 0)
            {
                var script = Path.Combine(dir.FullName, "deploy", "windows", "install.ps1");
                if (!File.Exists(script))
                    throw new FileNotFoundException($"Found repo root at '{dir.FullName}' but no install script at '{script}'.");
                return script;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not find Karmasis.MiniVault.sln walking up from '{AppContext.BaseDirectory}'.");
    }
}
