using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;

namespace MiniVault.Server.Auth;

public sealed record ClientIdentity(string ClientId, IReadOnlyList<string> Roles);

/// <summary>Clients, roles, and role rules. Every write path is a single SaveChangesAsync; no explicit transactions.</summary>
public sealed partial class ClientDirectory(MiniVaultDbContext db, TimeProvider clock)
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex IdPattern();

    public async Task<ClientIdentity?> AuthenticateAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        // Client-id existence is not treated as secret, so no dummy-hash work is done to mask an unknown id's timing.
        var client = await db.Clients.AsNoTracking().Include(c => c.Roles).SingleOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null || !client.Enabled) return null;
        if (!ClientSecretHasher.Verify(clientSecret, client.SecretHash, client.SecretSalt, client.SecretIterations)) return null;
        return new ClientIdentity(client.ClientId, client.Roles.Select(r => r.RoleName).ToList());
    }

    public async Task<IReadOnlyList<RoleRule>> GetRulesAsync(IEnumerable<string> roles, CancellationToken ct)
    {
        var roleList = roles.ToList();
        return await db.RoleRules.AsNoTracking().Where(r => roleList.Contains(r.RoleName)).ToListAsync(ct);
    }

    public async Task<string> AddClientAsync(string clientId, IEnumerable<string> roles, CancellationToken ct)
    {
        if (!IdPattern().IsMatch(clientId)) throw new ArgumentException($"Invalid client id '{clientId}'.", nameof(clientId));
        if (await db.Clients.AnyAsync(c => c.ClientId == clientId, ct)) throw new ArgumentException($"Client '{clientId}' already exists.", nameof(clientId));

        var roleList = roles.Distinct().ToList();
        if (roleList.Count > 0)
        {
            var existing = await db.Roles.Where(r => roleList.Contains(r.Name)).Select(r => r.Name).ToListAsync(ct);
            var missing = roleList.Except(existing).ToList();
            if (missing.Count > 0) throw new ArgumentException($"Unknown role(s): {string.Join(", ", missing)}.", nameof(roles));
        }

        var secret = ClientSecretHasher.GenerateSecret();
        var (hash, salt, iterations) = ClientSecretHasher.Hash(secret);
        var client = new Client
        {
            ClientId = clientId,
            SecretHash = hash,
            SecretSalt = salt,
            SecretIterations = iterations,
            Enabled = true,
            CreatedAt = clock.GetUtcNow(),
        };
        foreach (var role in roleList) client.Roles.Add(new ClientRole { ClientId = clientId, RoleName = role });
        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task RemoveClientAsync(string clientId, CancellationToken ct)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.ClientId == clientId, ct)
            ?? throw new ArgumentException($"Client '{clientId}' does not exist.", nameof(clientId));
        db.Clients.Remove(client);
        await db.SaveChangesAsync(ct);
    }

    public async Task AssignRoleAsync(string clientId, string role, CancellationToken ct)
    {
        if (!await db.Clients.AnyAsync(c => c.ClientId == clientId, ct)) throw new ArgumentException($"Client '{clientId}' does not exist.", nameof(clientId));
        if (!await db.Roles.AnyAsync(r => r.Name == role, ct)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        if (await db.ClientRoles.AnyAsync(cr => cr.ClientId == clientId && cr.RoleName == role, ct)) return;
        db.ClientRoles.Add(new ClientRole { ClientId = clientId, RoleName = role });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Enables or disables a client. A disabled client cannot obtain new tokens; tokens it already holds
    /// keep working until they expire.</summary>
    public async Task SetEnabledAsync(string clientId, bool enabled, CancellationToken ct)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.ClientId == clientId, ct)
            ?? throw new ArgumentException($"Client '{clientId}' does not exist.", nameof(clientId));
        if (client.Enabled == enabled) return;
        client.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<(string ClientId, bool Enabled, IReadOnlyList<string> Roles)>> ListClientsAsync(CancellationToken ct)
    {
        var clients = await db.Clients.AsNoTracking().Include(c => c.Roles).OrderBy(c => c.ClientId).ToListAsync(ct);
        return clients
            .Select(c => (c.ClientId, c.Enabled, (IReadOnlyList<string>)c.Roles.Select(r => r.RoleName).ToList()))
            .ToList();
    }

    public async Task AddRoleAsync(string name, string? description, CancellationToken ct)
    {
        if (!IdPattern().IsMatch(name)) throw new ArgumentException($"Invalid role name '{name}'.", nameof(name));
        if (await db.Roles.AnyAsync(r => r.Name == name, ct)) throw new ArgumentException($"Role '{name}' already exists.", nameof(name));
        db.Roles.Add(new Role { Name = name, Description = description });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveRoleAsync(string name, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Name == name, ct)
            ?? throw new ArgumentException($"Role '{name}' does not exist.", nameof(name));
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
    }

    public async Task GrantAsync(string role, string scope, Permission permission, CancellationToken ct)
    {
        if (!await db.Roles.AnyAsync(r => r.Name == role, ct)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        var rule = await db.RoleRules.SingleOrDefaultAsync(r => r.RoleName == role && r.Scope == scope, ct);
        if (rule is null)
        {
            rule = new RoleRule { RoleName = role, Scope = scope };
            db.RoleRules.Add(rule);
        }
        rule.Permission = permission;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken ct)
        => await db.Roles.AsNoTracking().Include(r => r.Rules).OrderBy(r => r.Name).ToListAsync(ct);
}
