using Microsoft.EntityFrameworkCore;
using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Infrastructure.Persistence;

public sealed class SealedTransferDbContext(DbContextOptions<SealedTransferDbContext> options) : DbContext(options)
{
    public DbSet<Transfer> Transfers => Set<Transfer>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

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

            // PostgreSQL: RowVersion maps to the xmin system column via IsRowVersion().
            // EF Core adds it to the WHERE clause, so a write based on stale data
            // affects zero rows and surfaces as a concurrency exception.
            transfer.Property(t => t.RowVersion).IsRowVersion();

            // Computed in C#, not stored.
            transfer.Ignore(t => t.DownloadsRemaining);
            transfer.Ignore(t => t.RemainingUploadBytes);

            // The cleanup job scans by expiry.
            transfer.HasIndex(t => t.ExpiresAt);
        });

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("AuditEvents");
            audit.HasKey(e => e.Id);
            audit.Property(e => e.Id).ValueGeneratedOnAdd();
            audit.HasIndex(e => new { e.TransferId, e.OccurredAt });
        });
    }
}
