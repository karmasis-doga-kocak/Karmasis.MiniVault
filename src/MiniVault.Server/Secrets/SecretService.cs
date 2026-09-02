using Microsoft.EntityFrameworkCore;
using MiniVault.Contracts;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;

namespace MiniVault.Server.Secrets;

public sealed class SecretService(MiniVaultDbContext db, SecretCipher cipher, TimeProvider clock)
{
    public const int MaxValueBytes = 1_048_576;

    public async Task<SecretRecord> GetAsync(string name, CancellationToken ct)
    {
        var row = await db.Secrets.AsNoTracking().SingleOrDefaultAsync(s => s.Name == name, ct) ?? throw new SecretNotFoundException(name);
        var value = await cipher.DecryptAsync(name, row.Ciphertext, row.DekVersion, ct);
        return new SecretRecord(row.Name, value, row.ContentType, row.Version, row.UpdatedAt);
    }

    public async Task<int?> GetVersionAsync(string name, CancellationToken ct)
        => await db.Secrets.AsNoTracking().Where(s => s.Name == name).Select(s => (int?)s.Version).SingleOrDefaultAsync(ct);

    public async Task<int> SetAsync(string name, byte[] value, string? contentType, string updatedBy, CancellationToken ct)
    {
        if (!SecretName.IsValid(name)) throw new ArgumentException("Invalid secret name.", nameof(name));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxValueBytes) throw new ArgumentException($"Secret value exceeds {MaxValueBytes} bytes.", nameof(value));
        if (contentType is { Length: > 128 }) throw new ArgumentException("contentType exceeds 128 characters.", nameof(contentType));

        var (ciphertext, dekVersion) = cipher.Encrypt(name, value);
        var now = clock.GetUtcNow();
        var row = await db.Secrets.SingleOrDefaultAsync(s => s.Name == name, ct);
        if (row is null)
        {
            row = new Secret { Name = name, Version = 1 };
            db.Secrets.Add(row);
        }
        else
        {
            row.Version += 1;
        }
        row.Ciphertext = ciphertext; row.DekVersion = dekVersion; row.ContentType = contentType; row.UpdatedAt = now; row.UpdatedBy = updatedBy;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex) { throw new SecretConflictException(name, ex); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("PK_Secrets") == true) { throw new SecretConflictException(name, ex); }
        return row.Version;
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        var row = await db.Secrets.SingleOrDefaultAsync(s => s.Name == name, ct) ?? throw new SecretNotFoundException(name);
        db.Secrets.Remove(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new SecretNotFoundException(name); }
    }

    public async Task<IReadOnlyList<SecretListItem>> ListAsync(string prefix, CancellationToken ct)
    {
        // The database collation is case-insensitive, so the LIKE-based StartsWith above can over-match on case;
        // re-filter in memory with an ordinal (case-sensitive) comparison to match SecretName's case-sensitive semantics.
        var candidates = await db.Secrets.AsNoTracking()
            .Where(s => s.Name.StartsWith(prefix))
            .OrderBy(s => s.Name)
            .Select(s => new SecretListItem { Name = s.Name, Version = s.Version, UpdatedAt = s.UpdatedAt })
            .ToListAsync(ct);
        return candidates.Where(i => i.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }
}
