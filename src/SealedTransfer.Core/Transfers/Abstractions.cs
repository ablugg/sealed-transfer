namespace SealedTransfer.Core.Transfers;

/// <summary>Persistence for transfers and audit events. Changes are written by <see cref="SaveChangesAsync"/>.</summary>
public interface ITransferRepository
{
    Task AddAsync(Transfer transfer, CancellationToken cancellationToken);

    Task<Transfer?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Transfer>> ListExpiredAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);

    void Remove(Transfer transfer);

    void AddAuditEvent(AuditEvent auditEvent);

    /// <exception cref="ConcurrencyConflictException">Another request changed the same transfer first.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Storage for encrypted chunks. Implementations never need to understand the content.</summary>
public interface IBlobStore
{
    /// <summary>Stores a chunk, replacing any earlier upload of the same index.</summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="BlobTooLargeException">The content is longer than <paramref name="maxBytes"/>.</exception>
    Task<long> WriteAsync(Guid transferId, int index, Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Opens a stored chunk, or returns null when it does not exist.</summary>
    Task<Stream?> OpenReadAsync(Guid transferId, int index, CancellationToken cancellationToken);

    Task DeleteAllAsync(Guid transferId, CancellationToken cancellationToken);
}

/// <summary>Issues and checks short-lived tokens that authorize fetching the chunks of one transfer.</summary>
public interface IDownloadTokenService
{
    string Issue(Guid transferId, TimeSpan lifetime);

    bool TryValidate(string? token, Guid transferId);
}

public sealed class ConcurrencyConflictException(Exception? innerException = null)
    : Exception("The transfer was changed by another request.", innerException);

public sealed class BlobTooLargeException(long maxBytes)
    : Exception($"The content is longer than the {maxBytes} bytes allowed.");
