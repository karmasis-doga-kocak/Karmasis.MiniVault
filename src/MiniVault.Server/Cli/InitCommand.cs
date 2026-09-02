using System.CommandLine;
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

        var command = new Command("init", "Initialize the vault: create the schema, master key, recovery material and first data key.");
        command.Options.Add(recovery);
        command.Options.Add(shares);
        command.Options.Add(threshold);
        command.Options.Add(masterKey);
        command.Options.Add(outFile);
        command.Validators.Add(result =>
        {
            if (result.GetValue(recovery) == "shamir" && (result.GetValue(shares) < 2 || result.GetValue(threshold) < 2))
                result.AddError("--shares and --threshold (both >= 2) are required for shamir recovery.");
        });

        command.SetAction(async (parseResult, ct) =>
        {
            var mode = parseResult.GetValue(recovery) == "shamir" ? RecoveryMode.Shamir : RecoveryMode.Single;
            var options = new InitOptions(mode, parseResult.GetValue(shares), parseResult.GetValue(threshold), parseResult.GetValue(masterKey));

            using var scope = services().CreateScope();
            var initializer = scope.ServiceProvider.GetRequiredService<VaultInitializer>();
            var provider = scope.ServiceProvider.GetRequiredService<IMasterKeyProvider>();
            var result = await initializer.InitializeAsync(options, ct);

            var text = RecoveryOutput.Format(result.Recovery, result.MasterKeyStored, result.MasterKeyBase64, provider.Name);
            await output.WriteAsync(text);
            var file = parseResult.GetValue(outFile);
            if (file is not null) await File.WriteAllTextAsync(file, text, ct);
            return 0;
        });
        return command;
    }
}
