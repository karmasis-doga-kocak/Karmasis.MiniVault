using System.Text.RegularExpressions;

namespace MiniVault.Server.Secrets;

public static partial class SecretName
{
    public const int MaxLength = 256;

    [GeneratedRegex("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? name) => !string.IsNullOrEmpty(name) && name.Length <= MaxLength && Pattern().IsMatch(name);
}
