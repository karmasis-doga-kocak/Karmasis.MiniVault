using System.Text.RegularExpressions;

namespace Karmasis.MiniVault.Server.Hosting;

/// <summary>
/// Adds the MiniVault configuration sources on top of the host defaults:
/// the machine-wide %ProgramData%\MiniVault\appsettings.json (Windows installs),
/// then environment variables and command-line arguments.
/// Environment variables and command-line arguments are re-added AFTER the machine-wide file on purpose: the host
/// defaults registered them earlier, and without re-adding them the ProgramData file would outrank them.
/// </summary>
public static partial class MiniVaultConfiguration
{
    public const string ProductFolderName = "MiniVault";

    public static string MachineConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductFolderName);

    public static IConfigurationBuilder AddMiniVaultConfiguration(this IConfigurationBuilder builder, string[] args)
    {
        builder.AddJsonFile(Path.Combine(MachineConfigDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
        builder.AddEnvironmentVariables();
        builder.AddCommandLine(ConfigurationOverrides(args));
        return builder;
    }

    /// <summary>A configuration override on the command line: <c>--Section:Key</c>, always colon-separated. The
    /// operator commands' own options (<c>--recovery</c>, <c>--force</c>, <c>--master-key-from-env</c>, ...) never
    /// contain a colon, which is what keeps the two vocabularies apart.</summary>
    [GeneratedRegex(@"^--[A-Za-z0-9_]+(:[A-Za-z0-9_]+)+$")]
    private static partial Regex ConfigurationOverrideTokenRegex();

    /// <summary>Just the <c>--Section:Key value</c> pairs, which is all <c>AddCommandLine</c> is meant to read.
    /// <para>The full args array must never be handed to <c>AddCommandLine</c>: it pairs every <c>--token</c> with
    /// whatever follows it, so a valueless CLI switch would either swallow the next argument (losing, say, a
    /// connection string override) or leave a bare value behind and make the provider throw
    /// <see cref="FormatException"/>.</para></summary>
    public static string[] ConfigurationOverrides(string[] args) => Split(args, keepOverrides: true);

    /// <summary>Everything that is not a <c>--Section:Key value</c> pair, for the command-line parser. Anything
    /// left over there that is not a known option is still a parse error.</summary>
    public static string[] WithoutConfigurationOverrides(string[] args) => Split(args, keepOverrides: false);

    private static string[] Split(string[] args, bool keepOverrides)
    {
        var result = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (!ConfigurationOverrideTokenRegex().IsMatch(args[i]))
            {
                if (!keepOverrides) result.Add(args[i]);
                continue;
            }

            // The token and the value that follows it belong together; both are kept or both are dropped.
            if (keepOverrides)
            {
                result.Add(args[i]);
                if (i + 1 < args.Length) result.Add(args[i + 1]);
            }
            i++;
        }
        return [.. result];
    }
}
