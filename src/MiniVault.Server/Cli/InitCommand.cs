using System.CommandLine;
using System.Security.AccessControl;
using System.Text;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Cli;

public static class InitCommand
{
    public static Command Build(Func<IServiceProvider> services, TextWriter output)
    {
        var recovery = new Option<string>("--recovery") { Description = "Recovery mode: single | shamir", Required = true };
        recovery.AcceptOnlyFromAmong("single", "shamir");
        var shares = new Option<int>("--shares") { Description = "Shamir: total number of shares (2..255)" };
        var threshold = new Option<int>("--threshold") { Description = "Shamir: shares needed to recover (2..shares)" };
        var masterKey = new Option<string?>("--master-key") { Description = "Derive the master key from this password instead of generating a random one" };
        var outFile = new Option<string?>("--out") { Description = "Also write the recovery output to this file" };
        var force = new Option<bool>("--force") { Description = "Overwrite an existing master key in the provider" };

        var command = new Command("init", "Initialize the vault: create the schema, master key, recovery material and first data key.");
        command.Options.Add(recovery);
        command.Options.Add(shares);
        command.Options.Add(threshold);
        command.Options.Add(masterKey);
        command.Options.Add(outFile);
        command.Options.Add(force);
        command.Validators.Add(result =>
        {
            if (result.GetValue(recovery) != "shamir") return;
            var sharesValue = result.GetValue(shares);
            var thresholdValue = result.GetValue(threshold);
            if (sharesValue < 2 || thresholdValue < 2)
            {
                result.AddError("--shares and --threshold (both >= 2) are required for shamir recovery.");
                return;
            }
            if (thresholdValue > sharesValue || sharesValue > 255)
                result.AddError("--threshold must be <= --shares and --shares must be <= 255.");
        });

        command.SetAction(async (parseResult, ct) =>
        {
            var mode = parseResult.GetValue(recovery) == "shamir" ? RecoveryMode.Shamir : RecoveryMode.Single;
            var options = new InitOptions(mode, parseResult.GetValue(shares), parseResult.GetValue(threshold), parseResult.GetValue(masterKey), parseResult.GetValue(force));

            // Checked before InitializeAsync runs so a bad --out costs nothing: no schema/key work happens for nothing.
            var file = parseResult.GetValue(outFile);
            if (file is not null && File.Exists(file))
                throw new VaultException($"Output file already exists: {file}");

            using var scope = services().CreateScope();
            var initializer = scope.ServiceProvider.GetRequiredService<VaultInitializer>();
            var provider = scope.ServiceProvider.GetRequiredService<IMasterKeyProvider>();
            var result = await initializer.InitializeAsync(options, ct);

            var text = RecoveryOutput.Format(result.Recovery, result.MasterKeyStored, result.MasterKeyBase64, provider.Name);
            await output.WriteAsync(text);
            if (file is not null) await WriteOutFileAsync(file, text, ct);
            return 0;
        });
        return command;
    }

    private static async Task WriteOutFileAsync(string file, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (OperatingSystem.IsWindows())
        {
            var security = WindowsFileAcl.CreateOwnerOnly();
            using var stream = new FileInfo(file).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.Asynchronous, security);
            await stream.WriteAsync(bytes, ct);
        }
        else
        {
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(bytes, ct);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
