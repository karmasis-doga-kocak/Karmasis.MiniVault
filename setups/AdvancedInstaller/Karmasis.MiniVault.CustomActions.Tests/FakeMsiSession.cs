using System;
using System.Collections.Generic;
using Karmasis.AdvancedInstallerKit;

namespace Karmasis.MiniVault.CustomActions.Tests
{
    /// <summary>
    /// In-memory IMsiSession. CustomActionData uses the same NAME="value", NAME2="value2" shape the
    /// installer's AI_DATA_SETTER rows produce, which is what MapCustomActionData&lt;T&gt; parses.
    /// </summary>
    internal sealed class FakeMsiSession : IMsiSession
    {
        public FakeMsiSession(string customActionData = null)
        {
            CustomActionData = customActionData ?? string.Empty;
        }

        public IntPtr MsiHandle
        {
            get { return IntPtr.Zero; }
        }

        public string CustomActionData { get; set; }

        public Dictionary<string, string> Properties { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<KeyValuePair<InstallMessage, string>> Messages { get; } =
            new List<KeyValuePair<InstallMessage, string>>();

        public List<string> Actions { get; } = new List<string>();

        public string GetProperty(string name)
        {
            string value;
            return Properties.TryGetValue(name, out value) ? value : string.Empty;
        }

        public void SetProperty(string name, string value)
        {
            Properties[name] = value;
        }

        public void Log(string message, InstallMessage level)
        {
            Messages.Add(new KeyValuePair<InstallMessage, string>(level, message));
        }

        public void DoAction(string action)
        {
            Actions.Add(action);
        }

        public IntPtr GetMsiWindowHandle()
        {
            return IntPtr.Zero;
        }

        public void SendMessage(string message, InstallMessage level)
        {
            Messages.Add(new KeyValuePair<InstallMessage, string>(level, message));
        }

        public bool HasMessage(InstallMessage level)
        {
            return Messages.Exists(m => m.Key == level);
        }

        public string LastMessage(InstallMessage level)
        {
            var index = Messages.FindLastIndex(m => m.Key == level);
            return index < 0 ? null : Messages[index].Value;
        }
    }

    /// <summary>Records the command lines RunInit builds and replays a canned result.</summary>
    internal sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessResult _result;

        public FakeProcessRunner(int exitCode = 0, string stdOut = "", string stdErr = "")
        {
            _result = new ProcessResult(exitCode, stdOut, stdErr);
        }

        public string LastExePath { get; private set; }

        public string[] LastArguments { get; private set; }

        public IDictionary<string, string> LastEnvironment { get; private set; }

        public int Invocations { get; private set; }

        public ProcessResult Run(string exePath, string[] arguments, IDictionary<string, string> environment)
        {
            LastExePath = exePath;
            LastArguments = arguments;
            LastEnvironment = environment;
            Invocations++;
            return _result;
        }
    }
}
