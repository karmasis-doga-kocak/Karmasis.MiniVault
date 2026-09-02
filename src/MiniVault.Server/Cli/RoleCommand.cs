using System.CommandLine;
using System.Text.RegularExpressions;
using MiniVault.Server.Audit;
using MiniVault.Server.Auth;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Cli;

public static partial class RoleCommand
{
    [GeneratedRegex("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]*)*$")]
    private static partial Regex ScopePattern();

    public static Command Build(Func<IServiceProvider> services, TextWriter output)
    {
        var role = new Command("role", "Manage roles (named lists of scope + permission rules).");

        var name = new Argument<string>("name") { Description = "Role name" };
        var description = new Option<string?>("--description") { Description = "Free text" };
        var add = new Command("add", "Create a role.");
        add.Arguments.Add(name); add.Options.Add(description);
        add.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            await scope.ServiceProvider.GetRequiredService<ClientDirectory>().AddRoleAsync(r.GetValue(name)!, r.GetValue(description), ct);
            await scope.ServiceProvider.GetRequiredService<AuditWriter>().WriteAsync(VaultInitializer.AuditClientId, "role.add", null, true, null, r.GetValue(name), ct);
            await output.WriteLineAsync($"Role created: {r.GetValue(name)}");
            return 0;
        });

        var remove = new Command("remove", "Delete a role, its rules and its assignments.");
        remove.Arguments.Add(name);
        remove.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            await scope.ServiceProvider.GetRequiredService<ClientDirectory>().RemoveRoleAsync(r.GetValue(name)!, ct);
            await scope.ServiceProvider.GetRequiredService<AuditWriter>().WriteAsync(VaultInitializer.AuditClientId, "role.remove", null, true, null, r.GetValue(name), ct);
            await output.WriteLineAsync($"Role removed: {r.GetValue(name)}");
            return 0;
        });

        var scopeOpt = new Option<string>("--scope") { Description = "Secret-name prefix, e.g. dataskope/collector/" };
        var allOpt = new Option<bool>("--all") { Description = "Grant on every secret (the empty scope). Required to leave --scope empty." };
        var permission = new Option<string>("--permission") { Description = "read | write (write includes read)", Required = true };
        permission.AcceptOnlyFromAmong("read", "write");
        var grant = new Command("grant", "Grant read or write on a scope to a role (replaces an existing rule for the same scope).");
        grant.Arguments.Add(name); grant.Options.Add(scopeOpt); grant.Options.Add(allOpt); grant.Options.Add(permission);
        grant.SetAction(async (r, ct) =>
        {
            // The empty scope grants the whole vault, which is easy to reach by mistake (a shell that expands
            // --scope "" away, a missing variable), so it has to be asked for explicitly with --all.
            var scopeValue = r.GetValue(scopeOpt) ?? "";
            if (scopeValue.Length == 0 && !r.GetValue(allOpt))
            {
                await output.WriteLineAsync("Error: --scope is required. Pass --all to grant on every secret (the empty scope).");
                return 1;
            }
            if (scopeValue.Length > 0 && (scopeValue.Length > Secret.MaxNameLength || !ScopePattern().IsMatch(scopeValue)))
            {
                await output.WriteLineAsync($"Error: invalid --scope '{scopeValue}'. Use up to {Secret.MaxNameLength} characters of letters, digits, '.', '_' or '-' in '/'-separated segments.");
                return 1;
            }
            var perm = r.GetValue(permission) == "write" ? Permission.Write : Permission.Read;
            using var scope = services().CreateScope();
            await scope.ServiceProvider.GetRequiredService<ClientDirectory>().GrantAsync(r.GetValue(name)!, scopeValue, perm, ct);
            await scope.ServiceProvider.GetRequiredService<AuditWriter>().WriteAsync(VaultInitializer.AuditClientId, "role.grant", null, true, null, $"{r.GetValue(name)} {scopeValue}={perm}", ct);
            await output.WriteLineAsync($"Granted {perm} on '{scopeValue}' to {r.GetValue(name)}");
            return 0;
        });

        var list = new Command("list", "List roles and their rules.");
        list.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            foreach (var role1 in await scope.ServiceProvider.GetRequiredService<ClientDirectory>().ListRolesAsync(ct))
            {
                var rules = role1.Rules.Count == 0 ? "(no rules)" : string.Join(", ", role1.Rules.OrderBy(x => x.Scope, StringComparer.Ordinal).Select(x => $"{x.Scope}={x.Permission}"));
                await output.WriteLineAsync($"{role1.Name}: {rules}");
            }
            return 0;
        });

        role.Subcommands.Add(add); role.Subcommands.Add(remove); role.Subcommands.Add(grant); role.Subcommands.Add(list);
        return role;
    }
}
