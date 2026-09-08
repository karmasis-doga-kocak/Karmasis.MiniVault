using System.CommandLine;
using Karmasis.MiniVault.Server.Keys;
using Karmasis.MiniVault.Server.Vault;

namespace Karmasis.MiniVault.Server.Cli;

public static class RecoverCommand
{
    public static Command Build(Func<IServiceProvider> services, TextWriter output)
    {
        var newMasterKey = new Option<string>("--new-master-key") { Description = "New master key password, or 'auto' to generate a random key", Required = true };
        var recoveryKey = new Option<string?>("--recovery-key") { Description = "Single mode: the recovery key" };
        var share = new Option<string[]>("--share") { Description = "Shamir mode: one share per --share (at least threshold)", AllowMultipleArgumentsPerToken = false };

        var command = new Command("recover", "Replace the master key using the recovery key or shares. Data keys are rewrapped; secrets are untouched.");
        command.Options.Add(newMasterKey);
        command.Options.Add(recoveryKey);
        command.Options.Add(share);
        command.Validators.Add(result =>
        {
            var hasKey = result.GetValue(recoveryKey) is not null;
            var hasShares = (result.GetValue(share) ?? []).Length > 0;
            if (hasKey == hasShares) result.AddError("Provide either --recovery-key or one or more --share values.");
        });

        command.SetAction(async (parseResult, ct) =>
        {
            var parts = parseResult.GetValue(recoveryKey) is { } key ? [key] : parseResult.GetValue(share)!.ToList();
            var password = parseResult.GetValue(newMasterKey);
            var options = new RecoverOptions(parts, string.Equals(password, "auto", StringComparison.OrdinalIgnoreCase) ? null : password);

            using var scope = services().CreateScope();
            var recovery = scope.ServiceProvider.GetRequiredService<VaultRecovery>();
            var provider = scope.ServiceProvider.GetRequiredService<IMasterKeyProvider>();
            var result = await recovery.RecoverAsync(options, ct);

            await output.WriteLineAsync($"Master key replaced. Data keys rewrapped: {result.DataKeysRewrapped}.");
            await output.WriteLineAsync(result.MasterKeyStored
                ? $"Master key stored by the {provider.Name} provider."
                : $"Master key (set as {EnvironmentMasterKeyProvider.VariableName} before starting the server): {result.MasterKeyBase64}");
            return 0;
        });
        return command;
    }
}
