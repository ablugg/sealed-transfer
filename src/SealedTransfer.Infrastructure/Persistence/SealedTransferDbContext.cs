using Microsoft.EntityFrameworkCore;
using SealedTransfer.Core.Identity;
using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Infrastructure.Persistence;

public sealed class SealedTransferDbContext(DbContextOptions<SealedTransferDbContext> options) : DbContext(options)
{
    public DbSet<Transfer> Transfers => Set<Transfer>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Organization> Organizations => Set<Organization>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transfer>(transfer =>
        {
            transfer.ToTable("Transfers");
            transfer.HasKey(t => t.Id);

            // Ids are generated in the application, not by the database.
            transfer.Property(t => t.Id).ValueGeneratedNever();

            transfer.Property(t => t.CryptoHeader).HasMaxLength(Transfer.MaxCryptoHeaderBytes).IsRequired();
            transfer.Property(t => t.EncryptedMetadata).HasMaxLength(Transfer.MaxEncryptedMetadataBytes).IsRequired();
            transfer.Property(t => t.OwnerTokenHash)
                .HasMaxLength(Transfer.OwnerTokenHashBytes)
                .IsFixedLength()
                .IsRequired();

            // PostgreSQL: RowVersion maps to the xmin system column for optimistic concurrency.
            // EF Core adds it to the WHERE clause, so a write based on stale data
            // affects zero rows and surfaces as a concurrency exception.
            transfer.Property(t => t.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .IsRowVersion()
                .ValueGeneratedOnAddOrUpdate();

            // Computed in C#, not stored.
            transfer.Ignore(t => t.DownloadsRemaining);
            transfer.Ignore(t => t.RemainingUploadBytes);

            // The cleanup job scans by expiry.
            transfer.HasIndex(t => t.ExpiresAt);

            // Optional FK to the user who created the transfer.
            transfer.HasIndex(t => t.CreatedByUserId);
        });

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("AuditEvents");
            audit.HasKey(e => e.Id);
            audit.Property(e => e.Id).ValueGeneratedOnAdd();
            audit.HasIndex(e => new { e.TransferId, e.OccurredAt });
        });

        modelBuilder.Entity<Organization>(org =>
        {
            org.ToTable("Organizations");
            org.HasKey(o => o.Id);
            org.Property(o => o.Id).ValueGeneratedNever();
            org.Property(o => o.Name).HasMaxLength(200).IsRequired();
            org.HasIndex(o => o.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");
        });

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("Users");
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedNever();
            user.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            user.Property(u => u.Email).HasMaxLength(320).IsRequired();
            user.HasIndex(u => u.Email).IsUnique();
            user.HasOne(u => u.Organization).WithMany(o => o.Members).HasForeignKey(u => u.OrganizationId);
        });
    }
}
