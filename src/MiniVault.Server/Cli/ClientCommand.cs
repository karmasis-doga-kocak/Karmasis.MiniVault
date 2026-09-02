using System.CommandLine;
using MiniVault.Server.Audit;
using MiniVault.Server.Auth;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Cli;

public static class ClientCommand
{
    public static Command Build(Func<IServiceProvider> services, TextWriter output)
    {
        var client = new Command("client", "Manage client identities (services that call MiniVault).");
        var id = new Argument<string>("id") { Description = "Client id, e.g. dataskope-collector" };
        var roles = new Option<string[]>("--role") { Description = "Role to assign (repeatable)" };

        var add = new Command("add", "Create a client and print its secret once.");
        add.Arguments.Add(id); add.Options.Add(roles);
        add.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            var secret = await scope.ServiceProvider.GetRequiredService<ClientDirectory>().AddClientAsync(r.GetValue(id)!, r.GetValue(roles) ?? [], ct);
            await scope.ServiceProvider.GetRequiredService<AuditWriter>().WriteAsync(VaultInitializer.AuditClientId, "client.add", null, true, null, r.GetValue(id), ct);
            await output.WriteLineAsync($"Client created: {r.GetValue(id)}");
            await output.WriteLineAsync($"Client secret: {secret}");
            await output.WriteLineAsync("Store this secret now; it is not shown again.");
            return 0;
        });

        var remove = new Command("remove", "Delete a client. Its tokens stop working when they expire (15 minutes by default).");
        remove.Arguments.Add(id);
        remove.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            await scope.ServiceProvider.GetRequiredService<ClientDirectory>().RemoveClientAsync(r.GetValue(id)!, ct);
            await scope.ServiceProvider.GetRequiredService<AuditWriter>().WriteAsync(VaultInitializer.AuditClientId, "client.remove", null, true, null, r.GetValue(id), ct);
            await output.WriteLineAsync($"Client removed: {r.GetValue(id)}");
            return 0;
        });

        var roleOpt = new Option<string>("--role") { Description = "Role to assign", Required = true };
        var assign = new Command("assign", "Assign a role to an existing client.");
        assign.Arguments.Add(id); assign.Options.Add(roleOpt);
        assign.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            await scope.ServiceProvider.GetRequiredService<ClientDirectory>().AssignRoleAsync(r.GetValue(id)!, r.GetValue(roleOpt)!, ct);
            await scope.ServiceProvider.GetRequiredService<AuditWriter>().WriteAsync(VaultInitializer.AuditClientId, "client.assign", null, true, null, $"{r.GetValue(id)} {r.GetValue(roleOpt)}", ct);
            await output.WriteLineAsync($"Assigned role {r.GetValue(roleOpt)} to {r.GetValue(id)}");
            return 0;
        });

        var enable = new Command("enable", "Re-enable a disabled client.");
        enable.Arguments.Add(id);
        enable.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            await scope.ServiceProvider.GetRequiredService<ClientDirectory>().SetEnabledAsync(r.GetValue(id)!, true, ct);
            await scope.ServiceProvider.GetRequiredService<AuditWriter>().WriteAsync(VaultInitializer.AuditClientId, "client.enable", null, true, null, r.GetValue(id), ct);
            await output.WriteLineAsync($"Client enabled: {r.GetValue(id)}");
            return 0;
        });

        var disable = new Command("disable", "Disable a client. It can no longer obtain tokens; tokens it already holds work until they expire.");
        disable.Arguments.Add(id);
        disable.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            await scope.ServiceProvider.GetRequiredService<ClientDirectory>().SetEnabledAsync(r.GetValue(id)!, false, ct);
            await scope.ServiceProvider.GetRequiredService<AuditWriter>().WriteAsync(VaultInitializer.AuditClientId, "client.disable", null, true, null, r.GetValue(id), ct);
            await output.WriteLineAsync($"Client disabled: {r.GetValue(id)}");
            return 0;
        });

        var list = new Command("list", "List clients and their roles.");
        list.SetAction(async (r, ct) =>
        {
            using var scope = services().CreateScope();
            foreach (var (clientId, enabled, clientRoles) in await scope.ServiceProvider.GetRequiredService<ClientDirectory>().ListClientsAsync(ct))
                await output.WriteLineAsync($"{clientId} [{(enabled ? "enabled" : "disabled")}]: {(clientRoles.Count == 0 ? "(no roles)" : string.Join(", ", clientRoles.OrderBy(x => x, StringComparer.Ordinal)))}");
            return 0;
        });

        client.Subcommands.Add(add); client.Subcommands.Add(remove); client.Subcommands.Add(assign);
        client.Subcommands.Add(enable); client.Subcommands.Add(disable); client.Subcommands.Add(list);
        return client;
    }
}
