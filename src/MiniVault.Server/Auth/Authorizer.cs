using MiniVault.Server.Data.Entities;

namespace MiniVault.Server.Auth;

/// <summary>Prefix-based permission check. A rule grants its scope prefix at its level; Write includes Read. Matching is ordinal and case-sensitive.</summary>
public static class Authorizer
{
    public static bool HasPermission(IEnumerable<RoleRule> rules, string secretName, Permission required)
        => rules.Any(r => r.Permission >= required && secretName.StartsWith(r.Scope, StringComparison.Ordinal));
}
