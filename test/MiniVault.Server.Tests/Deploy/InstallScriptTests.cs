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
