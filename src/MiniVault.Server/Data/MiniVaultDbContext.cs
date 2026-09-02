using Microsoft.EntityFrameworkCore;
using MiniVault.Server.Data.Entities;

namespace MiniVault.Server.Data;

public sealed class MiniVaultDbContext(DbContextOptions<MiniVaultDbContext> options) : DbContext(options)
{
    public DbSet<VaultMetadata> VaultMetadata => Set<VaultMetadata>();
    public DbSet<DataKey> DataKeys => Set<DataKey>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleRule> RoleRules => Set<RoleRule>();
    public DbSet<ClientRole> ClientRoles => Set<ClientRole>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<VaultMetadata>(e =>
        {
            e.ToTable("VaultMetadata");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RecoveryMode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RecoveryKeyWrappedByMaster).IsRequired();
        });

        b.Entity<DataKey>(e =>
        {
            e.ToTable("DataKeys");
            e.HasKey(x => x.Version);
            e.Property(x => x.Version).ValueGeneratedNever();
            e.Property(x => x.WrappedByMaster).IsRequired();
            e.Property(x => x.WrappedByRecovery).IsRequired();
            e.HasIndex(x => x.IsActive).IsUnique().HasFilter("[IsActive] = 1");
        });

        b.Entity<Secret>(e =>
        {
            e.ToTable("Secrets");
            e.HasKey(x => x.Name);
            // Secret names are case-sensitive: SecretCipher binds the requested name as AES-GCM associated data and
            // Authorizer compares scopes ordinally, so a case-insensitive database collation would let "a/B" resolve
            // the row stored as "a/b" (and then fail to decrypt, or bypass a scope check). BIN2 keeps SQL Server in step.
            e.Property(x => x.Name).HasMaxLength(Secret.MaxNameLength).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.Ciphertext).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(128);
            e.Property(x => x.UpdatedBy).HasMaxLength(128);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne<DataKey>().WithMany().HasForeignKey(x => x.DekVersion).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Client>(e =>
        {
            e.ToTable("Clients");
            e.HasKey(x => x.ClientId);
            e.Property(x => x.ClientId).HasMaxLength(128);
            e.Property(x => x.SecretHash).IsRequired();
            e.Property(x => x.SecretSalt).IsRequired();
            e.HasMany(x => x.Roles).WithOne(x => x.Client).HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("Roles");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(512);
            e.HasMany(x => x.Rules).WithOne().HasForeignKey(x => x.RoleName).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RoleRule>(e =>
        {
            e.ToTable("RoleRules");
            e.HasKey(x => x.Id);
            e.Property(x => x.RoleName).HasMaxLength(128);
            e.Property(x => x.Scope).HasMaxLength(Secret.MaxNameLength).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.Permission).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.RoleName, x.Scope }).IsUnique();
        });

        b.Entity<ClientRole>(e =>
        {
            e.ToTable("ClientRoles");
            e.HasKey(x => new { x.ClientId, x.RoleName });
            e.Property(x => x.ClientId).HasMaxLength(128);
            e.Property(x => x.RoleName).HasMaxLength(128);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleName).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.ClientId).HasMaxLength(128);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.SecretName).HasMaxLength(Secret.MaxNameLength);
            e.Property(x => x.RemoteIp).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(512);
            e.HasIndex(x => x.Timestamp);
        });
    }
}
