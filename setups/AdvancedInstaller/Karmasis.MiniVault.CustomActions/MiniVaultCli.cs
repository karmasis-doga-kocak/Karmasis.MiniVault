using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Karmasis.MiniVault.CustomActions
{
    /// <summary>Result of a single child-process run.</summary>
    public sealed class ProcessResult
    {
        public ProcessResult(int exitCode, string stdOut, string stdErr)
        {
            ExitCode = exitCode;
            StdOut = stdOut ?? string.Empty;
            StdErr = stdErr ?? string.Empty;
        }

        public int ExitCode { get; private set; }
        public string StdOut { get; private set; }
        public string StdErr { get; private set; }
    }

    /// <summary>
    /// Runs a native executable. Abstracted so the argument building in
    /// <see cref="MiniVaultCli"/> can be tested without launching minivault.exe.
    /// </summary>
    public interface IProcessRunner
    {
        /// <param name="environment">Extra environment variables for the child process, or null. Used to hand a
        /// secret to the child without putting it on a command line, where the process list, the MSI verbose log
        /// and any command-line auditing would all pick it up.</param>
        ProcessResult Run(string exePath, string[] arguments, IDictionary<string, string> environment);
    }

    /// <summary>
    /// Starts a process with both standard streams redirected and waits for it to exit.
    /// Mirrors Invoke-NativeProcess in deploy/windows/install.ps1.
    /// </summary>
    public sealed class ProcessRunner : IProcessRunner
    {
        private readonly int _timeoutMilliseconds;

        public ProcessRunner() : this(300000)
        {
        }

        public ProcessRunner(int timeoutMilliseconds)
        {
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        public ProcessResult Run(string exePath, string[] arguments)
        {
            return Run(exePath, arguments, null);
        }

        public ProcessResult Run(string exePath, string[] arguments, IDictionary<string, string> environment)
        {
            if (string.IsNullOrEmpty(exePath))
            {
                throw new ArgumentException("exePath is required.", "exePath");
            }

            var startInfo = new ProcessStartInfo(exePath, Quote(arguments))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (environment != null)
            {
                foreach (var entry in environment)
                {
                    startInfo.EnvironmentVariables[entry.Key] = entry.Value;
                }
            }

            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();

            using (var process = new Process())
            {
                process.StartInfo = startInfo;
                process.OutputDataReceived += (s, e) => { if (e.Data != null) { stdOut.AppendLine(e.Data); } };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) { stdErr.AppendLine(e.Data); } };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(_timeoutMilliseconds))
                {
                    try { process.Kill(); } catch { /* already gone */ }
                    throw new TimeoutException(
                        string.Format("'{0}' did not exit within {1} ms.", exePath, _timeoutMilliseconds));
                }

                // WaitForExit(int) does not guarantee the async output handlers have drained.
                process.WaitForExit();

                return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
            }
        }

        /// <summary>
        /// Joins arguments into a single command line using the CommandLineToArgvW quoting rules:
        /// wrap in double quotes when the argument contains whitespace, a quote or is empty, escape
        /// embedded quotes with a backslash, and double up the backslashes that precede a quote.
        /// </summary>
        public static string Quote(string[] arguments)
        {
            if (arguments == null || arguments.Length == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(arguments.Length);
            foreach (var argument in arguments)
            {
                parts.Add(QuoteOne(argument));
            }

            return string.Join(" ", parts.ToArray());
        }

        private static string QuoteOne(string argument)
        {
            if (argument == null)
            {
                return "\"\"";
            }

            var needsQuotes = argument.Length == 0 || argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) >= 0;
            if (!needsQuotes)
            {
                return argument;
            }

            var builder = new StringBuilder();
            builder.Append('"');

            for (var i = 0; i < argument.Length; i++)
            {
                var backslashes = 0;
                while (i < argument.Length && argument[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == argument.Length)
                {
                    // Trailing backslashes precede the closing quote, so they must be doubled.
                    builder.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[i] == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                }
                else
                {
                    builder.Append('\\', backslashes);
                    builder.Append(argument[i]);
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }

    /// <summary>Builds and runs 'minivault.exe' command lines.</summary>
    public static class MiniVaultCli
    {
        public const string ExecutableName = "minivault.exe";

        /// <summary>The variable 'minivault.exe init --master-key-from-env' reads the master-key password from.
        /// The password is passed this way instead of as '--master-key &lt;password&gt;' because a deferred custom
        /// action's command line is visible in the process list and is written to the MSI verbose log.</summary>
        public const string MasterKeyEnvironmentVariable = "MINIVAULT_INIT_MASTER_KEY";

        /// <summary>The environment the 'init' child process needs for <paramref name="masterKeyPassword"/>, or
        /// null when no password was given (the CLI then generates a random master key).</summary>
        public static IDictionary<string, string> BuildInitEnvironment(string masterKeyPassword)
        {
            if (string.IsNullOrEmpty(masterKeyPassword) || masterKeyPassword.Trim().Length == 0)
            {
                return null;
            }

            return new Dictionary<string, string> { { MasterKeyEnvironmentVariable, masterKeyPassword } };
        }

        /// <summary>
        /// Builds the argument vector for 'minivault.exe init', matching Step 4 of
        /// deploy/windows/install.ps1. A non-blank <paramref name="masterKeyPassword"/> adds
        /// '--master-key-from-env'; the password itself travels in the environment built by
        /// <see cref="BuildInitEnvironment"/>.
        /// </summary>
        public static string[] BuildInitArguments(string recovery, int shares, int threshold, string masterKeyPassword, string outFile)
        {
            if (string.IsNullOrEmpty(outFile))
            {
                throw new ArgumentException("outFile is required.", "outFile");
            }

            var mode = string.IsNullOrEmpty(recovery) ? "single" : recovery.Trim().ToLowerInvariant();
            if (mode != "single" && mode != "shamir")
            {
                throw new ArgumentException(
                    string.Format("Unknown recovery mode '{0}'; expected 'single' or 'shamir'.", recovery), "recovery");
            }

            var arguments = new List<string> { "init", "--recovery", mode, "--out", outFile };

            if (mode == "shamir")
            {
                if (shares < 2 || threshold < 2 || threshold > shares || shares > 255)
                {
                    throw new ArgumentException(string.Format(
                        "--recovery shamir requires shares and threshold (both >= 2, threshold <= shares <= 255); got shares={0}, threshold={1}.",
                        shares, threshold));
                }

                arguments.Add("--shares");
                arguments.Add(shares.ToString(System.Globalization.CultureInfo.InvariantCulture));
                arguments.Add("--threshold");
                arguments.Add(threshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(masterKeyPassword) && masterKeyPassword.Trim().Length > 0)
            {
                // The password itself goes through the environment (BuildInitEnvironment), never here.
                arguments.Add("--master-key-from-env");
            }

            return arguments.ToArray();
        }
    }
}
