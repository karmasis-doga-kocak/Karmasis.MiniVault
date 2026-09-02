namespace MiniVault.Server.Hosting;

/// <summary>
/// Adds the MiniVault configuration sources on top of the host defaults:
/// the machine-wide %ProgramData%\MiniVault\appsettings.json (Windows installs),
/// then environment variables and command-line arguments.
/// Environment variables and command-line arguments are re-added AFTER the machine-wide file on purpose: the host
/// defaults registered them earlier, and without re-adding them the ProgramData file would outrank them.
/// </summary>
public static class MiniVaultConfiguration
{
    public const string ProductFolderName = "MiniVault";

    public static string MachineConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductFolderName);

    public static IConfigurationBuilder AddMiniVaultConfiguration(this IConfigurationBuilder builder, string[] args)
    {
        builder.AddJsonFile(Path.Combine(MachineConfigDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
        builder.AddEnvironmentVariables();
        builder.AddCommandLine(args);
        return builder;
    }
}
