using System.Diagnostics;
using Karmasis.MiniVault.Server.Tests.TestDoubles;

namespace Karmasis.MiniVault.Server.Tests.Cli;

/// <summary>Runs the actual built minivault.dll as a child process, so the server's own runtimeconfig (not the test host's) is exercised.</summary>
public class RealBinarySmokeTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync(migrate: false);
    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string LocateServerDll()
    {
        // test bin: <repo>/test/Karmasis.MiniVault.Server.Tests/bin/<Config>/net10.0 -> server bin: <repo>/src/Karmasis.MiniVault.Server/bin/<Config>/net10.0/minivault.dll
        var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var config = new DirectoryInfo(testBin).Parent!.Name;               // Debug or Release
        var repoRoot = new DirectoryInfo(testBin).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var dll = Path.Combine(repoRoot, "src", "Karmasis.MiniVault.Server", "bin", config, "net10.0", "minivault.dll");
        File.Exists(dll).ShouldBeTrue($"server binary not found at {dll}; build the solution first");
        return dll;
    }

    [Fact]
    public async Task Init_WithRealBinary_Succeeds()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(LocateServerDll());
        psi.ArgumentList.Add("init");
        psi.ArgumentList.Add("--recovery"); psi.ArgumentList.Add("single");
        psi.ArgumentList.Add("--ConnectionStrings:MiniVault"); psi.ArgumentList.Add(_db.ConnectionString);
        psi.ArgumentList.Add("--MasterKey:Provider"); psi.ArgumentList.Add("Environment");
        psi.Environment.Remove("MINIVAULT__MASTERKEY");

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, $"stdout:\n{stdout}\nstderr:\n{stderr}");
        stdout.ShouldContain("Recovery key:");
        stdout.ShouldContain("MINIVAULT__MASTERKEY");
    }

    /// <summary>A startup failure has to read like an operator message, not a crash: exit code 3 (so a service
    /// restart loop and `sc.exe query` show something actionable), one line naming the setting at fault, and no
    /// stack frames.</summary>
    [Fact]
    public async Task Serve_WithMissingCertificate_ExitsWithCode3_AndReadableMessage()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(LocateServerDll());
        // No command word: this starts the server. No certificate is configured, so startup must fail.
        psi.ArgumentList.Add("--Tls:Url"); psi.ArgumentList.Add("https://127.0.0.1:0");
        psi.ArgumentList.Add("--ConnectionStrings:MiniVault"); psi.ArgumentList.Add(_db.ConnectionString);
        psi.ArgumentList.Add("--MasterKey:Provider"); psi.ArgumentList.Add("Environment");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment.Remove("MINIVAULT__MASTERKEY");

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = stdout + stderr;
        process.ExitCode.ShouldBe(3, output);
        output.ShouldContain("MiniVault cannot start");
        output.ShouldContain("Tls:Certificate");
        output.ShouldNotContain("   at ");
    }
}
