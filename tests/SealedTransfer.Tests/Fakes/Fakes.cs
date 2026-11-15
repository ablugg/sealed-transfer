using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Tests.Fakes;

/// <summary>A clock the test moves by hand, so expiry can be tested without waiting.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class InMemoryTransferRepository : ITransferRepository
{
    private readonly Dictionary<Guid, Transfer> _transfers = [];

    public List<AuditEvent> AuditEvents { get; } = [];

    public int Count => _transfers.Count;

    public Task AddAsync(Transfer transfer, CancellationToken cancellationToken)
    {
        _transfers.Add(transfer.Id, transfer);
        return Task.CompletedTask;
    }

    public Task<Transfer?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_transfers.GetValueOrDefault(id));

    public Task<IReadOnlyList<Transfer>> ListExpiredAsync(DateTimeOffset now, int max, CancellationToken cancellationToken)
    {
        IReadOnlyList<Transfer> expired = [.. _transfers.Values.Where(t => t.ExpiresAt <= now).Take(max)];
        return Task.FromResult(expired);
    }

    public void Remove(Transfer transfer) => _transfers.Remove(transfer.Id);

    public void AddAuditEvent(AuditEvent auditEvent) => AuditEvents.Add(auditEvent);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class InMemoryBlobStore : IBlobStore
{
    private readonly Dictionary<(Guid TransferId, int Index), byte[]> _blobs = [];

    public int Count => _blobs.Count;

    public async Task<long> WriteAsync(Guid transferId, int index, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > maxBytes)
        {
            throw new BlobTooLargeException(maxBytes);
        }

        _blobs[(transferId, index)] = buffer.ToArray();
        return buffer.Length;
    }

    public Task<Stream?> OpenReadAsync(Guid transferId, int index, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue((transferId, index), out var bytes) ? new MemoryStream(bytes) : null);

    public Task DeleteAllAsync(Guid transferId, CancellationToken cancellationToken)
    {
        foreach (var key in _blobs.Keys.Where(k => k.TransferId == transferId).ToList())
        {
            _blobs.Remove(key);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Tokens that simply name the transfer they belong to. Expiry is covered by the real service's own tests.</summary>
public sealed class FakeDownloadTokenService : IDownloadTokenService
{
    public string Issue(Guid transferId, TimeSpan lifetime) => $"token-for-{transferId:N}";

    public bool TryValidate(string? token, Guid transferId) => token == $"token-for-{transferId:N}";
}
