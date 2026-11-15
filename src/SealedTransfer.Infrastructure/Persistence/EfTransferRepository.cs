using Microsoft.EntityFrameworkCore;
using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Infrastructure.Persistence;

public sealed class EfTransferRepository(SealedTransferDbContext db) : ITransferRepository
{
    public async Task AddAsync(Transfer transfer, CancellationToken cancellationToken) =>
        await db.Transfers.AddAsync(transfer, cancellationToken);

    public Task<Transfer?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Transfers.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Transfer>> ListExpiredAsync(DateTimeOffset now, int max, CancellationToken cancellationToken) =>
        await db.Transfers
            .Where(t => t.ExpiresAt <= now)
            .OrderBy(t => t.ExpiresAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public void Remove(Transfer transfer) => db.Transfers.Remove(transfer);

    public void AddAuditEvent(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Drop everything this request was tracking. The caller must reload the transfer and
            // re-apply its change against current data instead of retrying with stale values.
            db.ChangeTracker.Clear();
            throw new ConcurrencyConflictException(exception);
        }
    }
}
