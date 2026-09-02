using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Cli;

/// <summary>Formats the one-time recovery output. Line prefixes ("Share N:", "Recovery key:") are parsed by tests and operators; keep them stable.</summary>
public static class RecoveryOutput
{
    public static string Format(RecoveryMaterial recovery, bool masterKeyStored, string? masterKeyBase64, string providerName)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("MiniVault initialized.");
        sb.AppendLine(recovery.Mode == RecoveryMode.Shamir
            ? $"Recovery mode: shamir ({recovery.Threshold} of {recovery.Shares})"
            : "Recovery mode: single");
        sb.AppendLine();
        sb.AppendLine("Store the following recovery material offline, in separate places. It is shown only once and is not saved anywhere.");
        if (recovery.Mode == RecoveryMode.Shamir)
            for (int i = 0; i < recovery.Parts.Count; i++)
                sb.AppendLine($"Share {i + 1}: {recovery.Parts[i]}");
        else
            sb.AppendLine($"Recovery key: {recovery.Parts[0]}");
        sb.AppendLine();
        sb.AppendLine(masterKeyStored
            ? $"Master key stored by the {providerName} provider."
            : $"Master key (set as {EnvironmentMasterKeyProvider.VariableName} before starting the server): {masterKeyBase64}");
        return sb.ToString();
    }
}
