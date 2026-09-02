using System.CommandLine;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Cli;

public static class RotateDekCommand
{
    public static Command Build(Func<IServiceProvider> services, TextWriter output)
    {
        var command = new Command("rotate-dek", "Create a new active data encryption key. Existing secrets stay readable with their old key.");
        command.SetAction(async (parseResult, ct) =>
        {
            using var scope = services().CreateScope();
            var version = await scope.ServiceProvider.GetRequiredService<VaultRecovery>().RotateDekAsync(ct);
            await output.WriteLineAsync($"Active data key version: {version}");
            return 0;
        });
        return command;
    }
}
