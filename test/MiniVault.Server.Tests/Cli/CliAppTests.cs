using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniVault.Server.Cli;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;

namespace MiniVault.Server.Tests.Cli;

public class CliAppTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private readonly InMemoryMasterKeyProvider _provider = new();
    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync(migrate: false);
    public async Task DisposeAsync() => await _db.DisposeAsync();

    private Task<(int Code, string Output)> Run(params string[] args) => Run(args, connectionString: null);

    private async Task<(int Code, string Output)> Run(string[] args, string? connectionString)
    {
        var output = new StringWriter();
        var code = await CliApp.RunAsync(
            [.. args, "--ConnectionStrings:MiniVault", connectionString ?? _db.ConnectionString],
            output,
            services => services.AddSingleton<IMasterKeyProvider>(_provider));
        return (code, output.ToString());
    }

    private async Task<bool> VaultMetadataExistsAsync()
    {
        await using var ctx = _db.CreateContext();
        await ctx.Database.MigrateAsync();
        return await ctx.VaultMetadata.AnyAsync();
    }

    private static List<string> Lines(string output, string prefix) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l[(l.IndexOf(':') + 1)..].Trim()).ToList();

    [Theory]
    [InlineData(new[] { "init" }, true)]
    [InlineData(new[] { "recover" }, true)]
    [InlineData(new[] { "rotate-dek" }, true)]
    [InlineData(new[] { "migrate" }, true)]
    [InlineData(new[] { "protect" }, true)]
    [InlineData(new[] { "--help" }, true)]
    [InlineData(new[] { "serve" }, false)]
    [InlineData(new string[0], false)]
    public void IsCliInvocation_DetectsCommands(string[] args, bool expected) => CliApp.IsCliInvocation(args).ShouldBe(expected);

    [Fact]
    public async Task Protect_PrintsAValueThatUnprotectsToTheInput()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (code, output) = await Run("protect", "--connection-string", "Server=sql01;Database=MiniVault;User ID=u;Password='p;w'");

        code.ShouldBe(0, output);
        var line = output.Trim();
        line.ShouldNotContain("sql01");
        MiniVault.Server.Hosting.ProtectedConfiguration.Unprotect(line).ShouldBe("Server=sql01;Database=MiniVault;User ID=u;Password='p;w'");
    }

    [Fact]
    public async Task Protect_RejectsADoubleQuote()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (code, output) = await Run("protect", "--connection-string", "Server=sql01;Password=\"x\"");

        code.ShouldBe(1);
        output.ShouldContain("double quote");
    }

    [Fact]
    public async Task Migrate_OnFreshDatabase_AppliesMigrations_ThenIsUpToDate_ThenInitStillWorks()
    {
        var (code, output) = await Run("migrate");
        code.ShouldBe(0);
        output.ShouldContain("Applied");

        var (code2, output2) = await Run("migrate");
        code2.ShouldBe(0);
        output2.ShouldContain("Database is up to date.");

        // The second run has nothing to apply, so its audit row must not be left with an empty Detail
        // (string.Join(", ", []) == "") - "none" is what an operator reading the audit log expects instead.
        await using var ctx = _db.CreateContext();
        var lastMigrateLog = await ctx.AuditLogs.Where(l => l.Action == "migrate").OrderByDescending(l => l.Id).FirstAsync();
        lastMigrateLog.Detail.ShouldBe("none");

        var (code3, output3) = await Run("init", "--recovery", "single");
        code3.ShouldBe(0);
        output3.ShouldContain("Recovery key");
    }

    [Fact]
    public async Task Init_Shamir_PrintsShares_AndSecondInitFails()
    {
        var (code, output) = await Run("init", "--recovery", "shamir", "--shares", "3", "--threshold", "2");

        code.ShouldBe(0);
        output.ShouldContain("Recovery mode: shamir (2 of 3)");
        Lines(output, "Share ").Count.ShouldBe(3);
        _provider.Exists().ShouldBeTrue();

        var (code2, output2) = await Run("init", "--recovery", "single");
        code2.ShouldBe(1);
        output2.ShouldContain("already initialized");
    }

    [Fact]
    public async Task Init_Single_WithOutFile_WritesFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"minivault-{Guid.NewGuid():N}.txt");
        try
        {
            var (code, output) = await Run("init", "--recovery", "single", "--master-key", "P@ss", "--out", file);

            code.ShouldBe(0);
            File.ReadAllText(file).ShouldBe(output);
            Lines(output, "Recovery key").Count.ShouldBe(1);
        }
        finally { File.Delete(file); }
    }

    /// <summary>The MSI and install.ps1 hand the master-key password to 'init' through the environment so it never
    /// appears on a command line. The derived KEK must be identical to the one --master-key would have produced,
    /// and the variable must be gone from the process once it has been read.</summary>
    [Fact]
    public async Task Init_WithMasterKeyFromEnv_DerivesKek()
    {
        const string password = "P@ssw0rd from the environment";
        Environment.SetEnvironmentVariable(InitCommand.MasterKeyEnvironmentVariable, password);
        try
        {
            var (code, output) = await Run("init", "--recovery", "single", "--master-key-from-env");

            code.ShouldBe(0, output);
            Environment.GetEnvironmentVariable(InitCommand.MasterKeyEnvironmentVariable).ShouldBeNull();

            await using var ctx = _db.CreateContext();
            var metadata = await ctx.VaultMetadata.SingleAsync();
            metadata.KekSalt.ShouldNotBeNull();
            metadata.KekIterations.ShouldNotBeNull();
            MasterKeyMaterial.FromPassword(password, metadata.KekSalt!, metadata.KekIterations!.Value).Kek
                .ShouldBe(_provider.GetKek());
        }
        finally
        {
            Environment.SetEnvironmentVariable(InitCommand.MasterKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Init_WithMasterKeyFromEnv_WhenTheVariableIsNotSet_IsAnError()
    {
        Environment.SetEnvironmentVariable(InitCommand.MasterKeyEnvironmentVariable, null);

        var (code, output) = await Run("init", "--recovery", "single", "--master-key-from-env");

        code.ShouldBe(1);
        output.ShouldContain(InitCommand.MasterKeyEnvironmentVariable);
    }

    [Fact]
    public async Task Recover_WithShares_ThenRotateDek()
    {
        var (_, initOutput) = await Run("init", "--recovery", "shamir", "--shares", "3", "--threshold", "2");
        var shares = Lines(initOutput, "Share ");
        var kekBefore = _provider.GetKek();

        var (code, output) = await Run("recover", "--new-master-key", "auto", "--share", shares[0], "--share", shares[2]);

        code.ShouldBe(0);
        output.ShouldContain("Master key replaced");
        _provider.GetKek().ShouldNotBe(kekBefore);

        var (code2, output2) = await Run("rotate-dek");
        code2.ShouldBe(0);
        output2.ShouldContain("Active data key version: 2");
    }

    [Fact]
    public async Task Recover_WrongShares_ReturnsError()
    {
        await Run("init", "--recovery", "shamir", "--shares", "3", "--threshold", "2");
        var bogus = Convert.ToBase64String(new byte[33]);

        var (code, output) = await Run("recover", "--new-master-key", "auto", "--share", bogus, "--share", bogus);

        code.ShouldBe(1);
        output.ShouldContain("Error:");
    }

    [Fact]
    public async Task Init_Shamir_MissingThreshold_IsParseError()
    {
        var (code, _) = await Run("init", "--recovery", "shamir", "--shares", "3");

        code.ShouldNotBe(0);
    }

    [Fact]
    public void StripConfigurationOverrides_RemovesConfigTokensAndValues()
    {
        var input = new[] { "init", "--recovery", "single", "--ConnectionStrings:MiniVault", "Server=x;Database=y", "--out", "f" };

        var result = CliApp.StripConfigurationOverrides(input);

        result.ShouldBe(["init", "--recovery", "single", "--out", "f"]);
    }

    [Fact]
    public async Task Init_UnknownOption_IsRejected()
    {
        var (code, _) = await Run("init", "--recovery", "single", "--uot", "x.txt");

        code.ShouldNotBe(0);
        (await VaultMetadataExistsAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_Shamir_ThresholdAboveShares_IsRejected()
    {
        var (code, output) = await Run("init", "--recovery", "shamir", "--shares", "2", "--threshold", "3");

        code.ShouldNotBe(0);
        output.ShouldContain("--threshold");
    }

    [Fact]
    public async Task Init_WithBadConnectionString_PrintsErrorLine()
    {
        var (code, output) = await Run(
            ["init", "--recovery", "single"],
            "Server=127.0.0.1,1;Database=x;Integrated Security=true;Connect Timeout=1;TrustServerCertificate=true");

        code.ShouldBe(1);
        output.ShouldContain("Error:");
        output.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Init_Single_WithExistingOutFile_Fails()
    {
        var file = Path.Combine(Path.GetTempPath(), $"minivault-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "existing");
        try
        {
            var (code, output) = await Run("init", "--recovery", "single", "--out", file);

            code.ShouldBe(1);
            output.ShouldContain("Error:");
            (await VaultMetadataExistsAsync()).ShouldBeFalse();
        }
        finally { File.Delete(file); }
    }
}
