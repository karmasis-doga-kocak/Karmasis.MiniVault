using System.CommandLine;
using Karmasis.MiniVault.Server.Hosting;
using Karmasis.MiniVault.Server.Keys;
using Karmasis.MiniVault.Server.Vault;

namespace Karmasis.MiniVault.Server.Cli;

/// <summary>
/// Entry point for the operator commands. Builds a plain generic host (no Kestrel) with the same
/// core services as the server, so 'init', 'recover' and 'rotate-dek' see exactly the server's configuration.
/// </summary>
public static class CliApp
{
    /// <summary>Every first argument that means "this is an operator command, not the server". The documentation
    /// consistency test reads this list, so every <c>minivault &lt;word&gt;</c> in the docs has to appear here.</summary>
    internal static readonly string[] CommandNames = ["init", "recover", "rotate-dek", "migrate", "protect", "client", "role", "--help", "-h", "-?", "--version"];

    /// <summary>Removes configuration-override tokens (e.g. --ConnectionStrings:MiniVault, --MasterKey:Provider) and their
    /// values from the args passed to System.CommandLine, so unknown CLI options are still rejected as parse errors.</summary>
    internal static string[] StripConfigurationOverrides(string[] args) =>
        MiniVaultConfiguration.WithoutConfigurationOverrides(args);

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && CommandNames.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, TextWriter output, Action<IServiceCollection>? configureServices = null)
    {
        // Only the configuration overrides reach the host's own AddCommandLine: it pairs each --token with the
        // next argument, so passing the CLI's own switches (--force, --master-key-from-env, ...) through would make
        // it swallow whatever follows them.
        var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = MiniVaultConfiguration.ConfigurationOverrides(args),
            DisableDefaults = false,
            ContentRootPath = AppContext.BaseDirectory,
        });
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
        root.Subcommands.Add(MigrateCommand.Build(Services, output));
        root.Subcommands.Add(ProtectCommand.Build(output));
        root.Subcommands.Add(ClientCommand.Build(Services, output));
        root.Subcommands.Add(RoleCommand.Build(Services, output));
        // Operators pass configuration overrides (--ConnectionStrings:MiniVault, --MasterKey:Provider, ...) on the same
        // command line; the configuration builder above consumed them. They are not CLI options, so they are stripped
        // from the args passed to System.CommandLine here; anything left over that is not a known option is still a
        // parse error.
        var parseResult = root.Parse(StripConfigurationOverrides(args));
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.WriteLineAsync($"Error: {ex.GetBaseException().Message}");
            return 1;
        }
    }
}
