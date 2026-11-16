using System.Security.Cryptography;

namespace SealedTransfer.Core.Transfers;

/// <summary>
/// One file transfer. The server never sees the file key or any plaintext: the crypto header and the
/// metadata are opaque bytes produced by the browser, and the chunks are AES-GCM ciphertext.
/// </summary>
public sealed class Transfer
{
    public const int MaxCryptoHeaderBytes = 64;
    public const int MaxEncryptedMetadataBytes = 4096;
    public const int OwnerTokenHashBytes = 32;

    /// <summary>Smallest possible chunk: an AES-GCM tag over an empty plaintext.</summary>
    public const int MinChunkBytes = 16;

    // EF Core materializes entities through this constructor.
    private Transfer()
    {
    }

    public Guid Id { get; private set; }

    public TransferStatus Status { get; private set; }

    /// <summary>Client-side crypto parameters (format version, nonce prefix, chunk size). Not secret.</summary>
    public byte[] CryptoHeader { get; private set; } = [];

    /// <summary>File name, type and size, encrypted in the browser.</summary>
    public byte[] EncryptedMetadata { get; private set; } = [];

    public int ChunkCount { get; private set; }

    /// <summary>Total ciphertext size the sender declared when creating the transfer.</summary>
    public long CiphertextBytes { get; private set; }

    public int UploadedChunks { get; private set; }

    public long UploadedBytes { get; private set; }

    /// <summary>SHA-256 of the owner token. The token itself is returned once and never stored.</summary>
    public byte[] OwnerTokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The Entra ID object id of the user who created this transfer, or null for anonymous.</summary>
    public Guid? CreatedByUserId { get; private set; }

    /// <summary>Null means unlimited downloads until expiry.</summary>
    public int? MaxDownloads { get; private set; }

    public int DownloadCount { get; private set; }

    /// <summary>True when the sender chose password protection. The server never sees the password.</summary>
    public bool PasswordProtected { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin), so two simultaneous downloads cannot both take the last slot.</summary>
    public uint RowVersion { get; private set; }

    public int? DownloadsRemaining =>
        MaxDownloads is null ? null : Math.Max(0, MaxDownloads.Value - DownloadCount);

    public static Transfer Create(
        Guid id,
        byte[] cryptoHeader,
        byte[] encryptedMetadata,
        int chunkCount,
        long ciphertextBytes,
        byte[] ownerTokenHash,
        DateTimeOffset now,
        TimeSpan lifetime,
        int? maxDownloads,
        bool passwordProtected = false,
        Guid? createdByUserId = null)
    {
        ArgumentNullException.ThrowIfNull(cryptoHeader);
        ArgumentNullException.ThrowIfNull(encryptedMetadata);
        ArgumentNullException.ThrowIfNull(ownerTokenHash);

        if (cryptoHeader.Length is 0 or > MaxCryptoHeaderBytes)
        {
            throw new ArgumentException("Crypto header is empty or too long.", nameof(cryptoHeader));
        }

        if (encryptedMetadata.Length is 0 or > MaxEncryptedMetadataBytes)
        {
            throw new ArgumentException("Encrypted metadata is empty or too long.", nameof(encryptedMetadata));
        }

        if (ownerTokenHash.Length != OwnerTokenHashBytes)
        {
            throw new ArgumentException("Owner token hash must be a SHA-256 digest.", nameof(ownerTokenHash));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(chunkCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ciphertextBytes, (long)chunkCount * MinChunkBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        if (maxDownloads is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDownloads), "Download limit must be at least 1.");
        }

        return new Transfer
        {
            Id = id,
            Status = TransferStatus.Uploading,
            CryptoHeader = cryptoHeader,
            EncryptedMetadata = encryptedMetadata,
            ChunkCount = chunkCount,
            CiphertextBytes = ciphertextBytes,
            OwnerTokenHash = ownerTokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            MaxDownloads = maxDownloads,
            PasswordProtected = passwordProtected,
            CreatedByUserId = createdByUserId,
        };
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>True when a new download may start right now.</summary>
    public bool IsAvailable(DateTimeOffset now) =>
        Status == TransferStatus.Ready
        && !IsExpired(now)
        && (MaxDownloads is null || DownloadCount < MaxDownloads);

    /// <summary>Constant-time comparison, so response timing does not reveal how much of a guess matched.</summary>
    public bool OwnerTokenHashMatches(ReadOnlySpan<byte> candidateHash) =>
        CryptographicOperations.FixedTimeEquals(OwnerTokenHash, candidateHash);

    /// <summary>How many bytes the next chunk may contain before the declared total is exceeded.</summary>
    public long RemainingUploadBytes => CiphertextBytes - UploadedBytes;

    /// <summary>Checks that chunk <paramref name="index"/> is the next one expected. Call before storing it.</summary>
    public TransferError? CanAcceptChunk(int index, DateTimeOffset now)
    {
        if (Status != TransferStatus.Uploading)
        {
            return TransferError.InvalidState;
        }

        if (IsExpired(now))
        {
            return TransferError.Gone;
        }

        if (index != UploadedChunks || index >= ChunkCount)
        {
            return TransferError.Conflict;
        }

        return RemainingUploadBytes < MinChunkBytes ? TransferError.TooLarge : null;
    }

    /// <summary>Records a chunk that has been stored.</summary>
    public TransferError? RecordChunk(int index, long bytes, DateTimeOffset now)
    {
        if (CanAcceptChunk(index, now) is { } error)
        {
            return error;
        }

        if (bytes < MinChunkBytes)
        {
            return TransferError.Invalid;
        }

        if (bytes > RemainingUploadBytes)
        {
            return TransferError.TooLarge;
        }

        UploadedChunks++;
        UploadedBytes += bytes;
        return null;
    }

    /// <summary>Marks the upload finished. Every declared chunk and byte must have arrived.</summary>
    public TransferError? Complete(DateTimeOffset now)
    {
        if (Status != TransferStatus.Uploading)
        {
            return TransferError.InvalidState;
        }

        if (IsExpired(now))
        {
            return TransferError.Gone;
        }

        if (UploadedChunks != ChunkCount || UploadedBytes != CiphertextBytes)
        {
            return TransferError.InvalidState;
        }

        Status = TransferStatus.Ready;
        return null;
    }

    /// <summary>Counts one download against the limit.</summary>
    public TransferError? StartDownload(DateTimeOffset now)
    {
        if (!IsAvailable(now))
        {
            return TransferError.Gone;
        }

        DownloadCount++;
        return null;
    }

    public void Revoke() => Status = TransferStatus.Revoked;
}
