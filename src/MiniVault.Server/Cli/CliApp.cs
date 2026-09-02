using System.CommandLine;
using System.Text.RegularExpressions;
using MiniVault.Server.Hosting;
using MiniVault.Server.Keys;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Cli;

/// <summary>
/// Entry point for the operator commands. Builds a plain generic host (no Kestrel) with the same
/// core services as the server, so 'init', 'recover' and 'rotate-dek' see exactly the server's configuration.
/// </summary>
public static partial class CliApp
{
    private static readonly string[] CommandNames = ["init", "recover", "rotate-dek", "client", "role", "--help", "-h", "-?", "--version"];

    [GeneratedRegex(@"^--[A-Za-z0-9_]+(:[A-Za-z0-9_]+)+$")]
    private static partial Regex ConfigurationOverrideTokenRegex();

    /// <summary>Removes configuration-override tokens (e.g. --ConnectionStrings:MiniVault, --MasterKey:Provider) and their
    /// values from the args passed to System.CommandLine, so unknown CLI options are still rejected as parse errors.</summary>
    internal static string[] StripConfigurationOverrides(string[] args)
    {
        var result = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (ConfigurationOverrideTokenRegex().IsMatch(args[i]))
            {
                i++; // skip the value that follows
                continue;
            }
            result.Add(args[i]);
        }
        return [.. result];
    }

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && CommandNames.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, TextWriter output, Action<IServiceCollection>? configureServices = null)
    {
        var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = false, ContentRootPath = AppContext.BaseDirectory });
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
        root.Subcommands.Add(ClientCommand.Build(Services, output));
        root.Subcommands.Add(RoleCommand.Build(Services, output));
        // Operators pass configuration overrides (--ConnectionStrings:MiniVault, --MasterKey:Provider, ...) on the same
        // command line; AddCommandLine(args) above consumes them from the full args. They are not CLI options, so they
        // are stripped from the args passed to System.CommandLine here; anything left over that is not a known option
        // is still a parse error.
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
