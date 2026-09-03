using Microsoft.EntityFrameworkCore;
using Karmasis.MiniVault.Contracts;
using Karmasis.MiniVault.Server.Data;
using Karmasis.MiniVault.Server.Data.Entities;

namespace Karmasis.MiniVault.Server.Secrets;

public sealed class SecretService(MiniVaultDbContext db, SecretCipher cipher, TimeProvider clock)
{
    public const int MaxValueBytes = 1_048_576;

    /// <summary>Test-only seam: awaited with the secret name immediately before the SaveChangesAsync in
    /// <see cref="SetAsync"/> so a test can stage a competing write. The name lets a hook ignore writes from
    /// tests running in parallel.</summary>
    internal static Func<string, Task>? BeforeSaveHook;

    public async Task<SecretRecord> GetAsync(string name, CancellationToken ct)
    {
        var row = await db.Secrets.AsNoTracking().SingleOrDefaultAsync(s => s.Name == name, ct) ?? throw new SecretNotFoundException(name);
        // Defence in depth for databases created before the BIN2 collation: a case-insensitive column would hand back
        // "a/b" for a request for "a/B", and the name is the cipher's associated data, so fail closed instead.
        if (!string.Equals(row.Name, name, StringComparison.Ordinal)) throw new SecretNotFoundException(name);
        var value = await cipher.DecryptAsync(name, row.Ciphertext, row.DekVersion, ct);
        return new SecretRecord(row.Name, value, row.ContentType, row.Version, row.UpdatedAt);
    }

    public async Task<int?> GetVersionAsync(string name, CancellationToken ct)
    {
        var row = await db.Secrets.AsNoTracking().Where(s => s.Name == name).Select(s => new { s.Name, s.Version }).SingleOrDefaultAsync(ct);
        return row is null || !string.Equals(row.Name, name, StringComparison.Ordinal) ? null : row.Version;
    }

    public async Task<int> SetAsync(string name, byte[] value, string? contentType, string updatedBy, CancellationToken ct)
    {
        if (!SecretName.IsValid(name)) throw new SecretValidationException("Invalid secret name.");
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxValueBytes) throw new SecretValidationException($"Secret value exceeds {MaxValueBytes} bytes.");
        if (contentType is { Length: > 128 }) throw new SecretValidationException("contentType exceeds 128 characters.");

        var (ciphertext, dekVersion) = cipher.Encrypt(name, value);
        var now = clock.GetUtcNow();
        var row = await db.Secrets.SingleOrDefaultAsync(s => s.Name == name, ct);
        // A case-insensitive collation can match a row stored under a different case. Treating that as "no row" would
        // insert a duplicate that the primary key rejects, so report the conflict instead of writing over a sibling.
        if (row is not null && !string.Equals(row.Name, name, StringComparison.Ordinal)) throw SecretConflictException.CaseVariant(name);
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
        if (BeforeSaveHook is { } hook) await hook(name);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex) { throw new SecretConflictException(name, ex); }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2627 or 2601 }) { throw new SecretConflictException(name, ex); }
        return row.Version;
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        var row = await db.Secrets.SingleOrDefaultAsync(s => s.Name == name, ct) ?? throw new SecretNotFoundException(name);
        if (!string.Equals(row.Name, name, StringComparison.Ordinal)) throw new SecretNotFoundException(name);
        db.Secrets.Remove(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new SecretNotFoundException(name); }
    }

    public async Task<IReadOnlyList<SecretListItem>> ListAsync(string prefix, CancellationToken ct)
    {
        // Databases created before the collation fix use a legacy case-insensitive collation, so the LIKE-based
        // StartsWith above can over-match on case there; re-filter in memory with an ordinal (case-sensitive)
        // comparison to match SecretName's case-sensitive semantics. This also acts as a fail-closed safety net
        // if collation configuration ever drifts.
        var candidates = await db.Secrets.AsNoTracking()
            .Where(s => s.Name.StartsWith(prefix))
            .OrderBy(s => s.Name)
            .Select(s => new SecretListItem { Name = s.Name, Version = s.Version, UpdatedAt = s.UpdatedAt })
            .ToListAsync(ct);
        return candidates.Where(i => i.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }
}
