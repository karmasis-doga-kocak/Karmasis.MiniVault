using System.CommandLine;
using Karmasis.MiniVault.Server.Hosting;
using Karmasis.MiniVault.Server.Vault;

namespace Karmasis.MiniVault.Server.Cli;

/// <summary>
/// Prints the DPAPI-protected form of a connection string for <c>ConnectionStrings:MiniVaultProtected</c>.
/// Run it on the host that will use the value: DPAPI (LocalMachine) binds the output to this machine.
/// The installer and install.ps1 produce the same value themselves; this command is for a hand-edited
/// configuration and for a restore onto a new host.
/// </summary>
public static class ProtectCommand
{
    public static Command Build(TextWriter output)
    {
        var connectionString = new Option<string>("--connection-string")
        {
            Description = "The plain connection string to protect. Interactive use only: it is visible on the command line to anything that can list processes.",
            Required = true,
        };

        var command = new Command("protect", "Protect a connection string with DPAPI (LocalMachine) for ConnectionStrings:MiniVaultProtected. Windows only; the output is bound to this machine.");
        command.Options.Add(connectionString);
        command.SetAction(async (parseResult, ct) =>
        {
            if (!OperatingSystem.IsWindows())
                throw new VaultException("protect requires Windows (DPAPI). On Linux configure ConnectionStrings:MiniVault directly, from a mounted secret.");
            var value = parseResult.GetValue(connectionString)!;
            if (value.IndexOf('"') >= 0)
                throw new VaultException("The connection string must not contain a double quote; use single quotes around a value that needs quoting.");
            await output.WriteLineAsync(ProtectedConfiguration.Protect(value));
            return 0;
        });
        return command;
    }
}
