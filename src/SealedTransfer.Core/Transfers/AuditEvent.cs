namespace SealedTransfer.Core.Transfers;

public enum AuditEventType
{
    Created = 1,
    UploadCompleted = 2,
    DownloadStarted = 3,
    Revoked = 4,
    Purged = 5,
}

/// <summary>
/// An append-only record of something that happened to a transfer. It has no foreign key to
/// <see cref="Transfer"/> on purpose: the audit trail must outlive the transfer it describes.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public long Id { get; private set; }

    public Guid TransferId { get; private set; }

    public AuditEventType Type { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static AuditEvent Create(Guid transferId, AuditEventType type, DateTimeOffset occurredAt) =>
        new() { TransferId = transferId, Type = type, OccurredAt = occurredAt };
}
