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

    private async Task<(int Code, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        var code = await CliApp.RunAsync(
            [.. args, "--ConnectionStrings:MiniVault", _db.ConnectionString],
            output,
            services => services.AddSingleton<IMasterKeyProvider>(_provider));
        return (code, output.ToString());
    }

    private static List<string> Lines(string output, string prefix) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l[(l.IndexOf(':') + 1)..].Trim()).ToList();

    [Theory]
    [InlineData(new[] { "init" }, true)]
    [InlineData(new[] { "recover" }, true)]
    [InlineData(new[] { "rotate-dek" }, true)]
    [InlineData(new[] { "--help" }, true)]
    [InlineData(new[] { "serve" }, false)]
    [InlineData(new string[0], false)]
    public void IsCliInvocation_DetectsCommands(string[] args, bool expected) => CliApp.IsCliInvocation(args).ShouldBe(expected);

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
}
