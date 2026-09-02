using System.CommandLine;
using System.Security.AccessControl;
using System.Text;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Cli;

public static class InitCommand
{
    /// <summary>Environment variable read by <c>--master-key-from-env</c>. A password passed this way never
    /// reaches a command line, so it cannot be read out of the process list, a Windows service's ImagePath, an
    /// MSI verbose log or a shell history file. It is removed from this process's environment as soon as it is
    /// read, so nothing this process starts inherits it.</summary>
    public const string MasterKeyEnvironmentVariable = "MINIVAULT_INIT_MASTER_KEY";

    public static Command Build(Func<IServiceProvider> services, TextWriter output)
    {
        var recovery = new Option<string>("--recovery") { Description = "Recovery mode: single | shamir", Required = true };
        recovery.AcceptOnlyFromAmong("single", "shamir");
        var shares = new Option<int>("--shares") { Description = "Shamir: total number of shares (2..255)" };
        var threshold = new Option<int>("--threshold") { Description = "Shamir: shares needed to recover (2..shares)" };
        var masterKey = new Option<string?>("--master-key") { Description = "Derive the master key from this password instead of generating a random one. Interactive use only: the password is visible on the command line to anyone who can list processes, and to anything that logs command lines. Prefer --master-key-from-env for unattended installs." };
        var masterKeyFromEnv = new Option<bool>("--master-key-from-env") { Description = $"Derive the master key from the {MasterKeyEnvironmentVariable} environment variable instead of from --master-key, so the password never appears on a command line." };
        var outFile = new Option<string?>("--out") { Description = "Also write the recovery output to this file" };
        var force = new Option<bool>("--force") { Description = "Overwrite an existing master key in the provider" };

        var command = new Command("init", "Initialize the vault: create the schema, master key, recovery material and first data key.");
        command.Options.Add(recovery);
        command.Options.Add(shares);
        command.Options.Add(threshold);
        command.Options.Add(masterKey);
        command.Options.Add(masterKeyFromEnv);
        command.Options.Add(outFile);
        command.Options.Add(force);
        command.Validators.Add(result =>
        {
            if (result.GetValue(masterKeyFromEnv) && !string.IsNullOrEmpty(result.GetValue(masterKey)))
                result.AddError("--master-key and --master-key-from-env cannot both be given.");
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
            var masterKeyPassword = parseResult.GetValue(masterKeyFromEnv)
                ? ReadMasterKeyFromEnvironment()
                : parseResult.GetValue(masterKey);
            var options = new InitOptions(mode, parseResult.GetValue(shares), parseResult.GetValue(threshold), masterKeyPassword, parseResult.GetValue(force));

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

    /// <summary>Reads the master-key password out of the environment and removes it from this process, so it is
    /// not inherited by anything started later and is not visible in a dump of the process environment.</summary>
    private static string ReadMasterKeyFromEnvironment()
    {
        var password = Environment.GetEnvironmentVariable(MasterKeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(MasterKeyEnvironmentVariable, null);
        if (string.IsNullOrEmpty(password))
            throw new VaultException($"--master-key-from-env was given but {MasterKeyEnvironmentVariable} is not set.");
        return password;
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
