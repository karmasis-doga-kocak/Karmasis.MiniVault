using System.CommandLine;
using MiniVault.Server.Hosting;
using MiniVault.Server.Keys;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Cli;

/// <summary>
/// Entry point for the operator commands. Builds a plain generic host (no Kestrel) with the same
/// core services as the server, so 'init', 'recover' and 'rotate-dek' see exactly the server's configuration.
/// </summary>
public static class CliApp
{
    private static readonly string[] CommandNames = ["init", "recover", "rotate-dek", "--help", "-h", "-?", "--version"];

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && CommandNames.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, TextWriter output, Action<IServiceCollection>? configureServices = null)
    {
        var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = false });
        hostBuilder.Configuration.AddMiniVaultConfiguration(args);
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services.AddMiniVaultCore(hostBuilder.Configuration);
        configureServices?.Invoke(hostBuilder.Services);
        using var host = hostBuilder.Build();
        IServiceProvider Services() => host.Services;

        var root = new RootCommand("MiniVault operator commands. Run without a command to start the server.");
        root.Subcommands.Add(InitCommand.Build(Services, output));
        root.Subcommands.Add(RecoverCommand.Build(Services, output));
        root.Subcommands.Add(RotateDekCommand.Build(Services, output));
        // Operators pass configuration overrides (--ConnectionStrings:MiniVault, --MasterKey:Provider, ...) on the same
        // command line; AddCommandLine(args) above consumes them from the full args. They are not CLI options, so
        // unmatched tokens must not be treated as parse errors here (validator errors, e.g. missing --threshold, still are).
        root.TreatUnmatchedTokensAsErrors = false;

        var parseResult = root.Parse(args);
        try
        {
            // Default handler disabled so VaultException/MasterKeyUnavailableException reach the catch blocks below
            // and are printed as a single "Error: ..." line instead of a stack trace.
            return await parseResult.InvokeAsync(new InvocationConfiguration { Output = output, Error = output, EnableDefaultExceptionHandler = false });
        }
        catch (VaultException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
        catch (MasterKeyUnavailableException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }
}
