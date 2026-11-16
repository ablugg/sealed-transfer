using System.Buffers.Text;
using System.Security.Cryptography;

namespace SealedTransfer.Core.Transfers;

/// <summary>
/// The application logic for transfers. It enforces every rule on the server side and assumes the
/// caller is hostile: sizes, counts, order and tokens are all checked here, never trusted.
/// </summary>
public sealed class TransferService(
    ITransferRepository repository,
    IBlobStore blobs,
    IDownloadTokenService downloadTokens,
    TransferOptions options,
    TimeProvider clock)
{
    private const int OwnerTokenBytes = 32;
    private const int MaxSaveAttempts = 3;
    private const int PurgeBatchSize = 100;

    public async Task<Result<CreatedTransfer>> CreateAsync(CreateTransferCommand command, CancellationToken cancellationToken)
    {
        if (Validate(command) is { } problem)
        {
            return Result<CreatedTransfer>.Fail(problem.Error, problem.Detail);
        }

        var now = clock.GetUtcNow();
        var ownerToken = RandomNumberGenerator.GetBytes(OwnerTokenBytes);

        var transfer = Transfer.Create(
            Guid.NewGuid(),
            command.CryptoHeader,
            command.EncryptedMetadata,
            command.ChunkCount,
            command.CiphertextBytes,
            SHA256.HashData(ownerToken),
            now,
            TimeSpan.FromHours(command.ExpiresInHours),
            command.MaxDownloads,
            command.PasswordProtected,
            command.UserId);

        await repository.AddAsync(transfer, cancellationToken);
        repository.AddAuditEvent(AuditEvent.Create(transfer.Id, AuditEventType.Created, now));
        await repository.SaveChangesAsync(cancellationToken);

        return Result<CreatedTransfer>.Ok(
            new CreatedTransfer(transfer.Id, Base64Url.EncodeToString(ownerToken), transfer.ExpiresAt));
    }

    public async Task<Result> UploadChunkAsync(
        Guid id,
        string? ownerToken,
        int index,
        Stream content,
        CancellationToken cancellationToken)
    {
        var owned = await FindOwnedAsync(id, ownerToken, cancellationToken);
        if (!owned.IsSuccess)
        {
            return Result.Fail(owned.Error!.Value);
        }

        var transfer = owned.Value!;
        var now = clock.GetUtcNow();
        if (transfer.CanAcceptChunk(index, now) is { } refusal)
        {
            return Result.Fail(refusal, DescribeChunkRefusal(refusal, transfer));
        }

        // Never read more than the transfer still has room for, whatever the client sends.
        var limit = Math.Min(options.MaxChunkBytes, transfer.RemainingUploadBytes);

        long written;
        try
        {
            written = await blobs.WriteAsync(id, index, content, limit, cancellationToken);
        }
        catch (BlobTooLargeException)
        {
            return Result.Fail(TransferError.TooLarge, $"Chunk {index} is larger than the {limit} bytes allowed.");
        }

        if (transfer.RecordChunk(index, written, now) is { } error)
        {
            return Result.Fail(error, $"Chunk {index} was rejected.");
        }

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Result.Fail(TransferError.Conflict, "The transfer was changed by another request.");
        }

        return Result.Ok();
    }

    public async Task<Result> CompleteAsync(Guid id, string? ownerToken, CancellationToken cancellationToken)
    {
        var owned = await FindOwnedAsync(id, ownerToken, cancellationToken);
        if (!owned.IsSuccess)
        {
            return Result.Fail(owned.Error!.Value);
        }

        var transfer = owned.Value!;
        var now = clock.GetUtcNow();
        if (transfer.Complete(now) is { } error)
        {
            return Result.Fail(error, "The upload is not complete, or the transfer is no longer accepting changes.");
        }

        repository.AddAuditEvent(AuditEvent.Create(id, AuditEventType.UploadCompleted, now));

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Result.Fail(TransferError.Conflict, "The transfer was changed by another request.");
        }

        return Result.Ok();
    }

    public async Task<Result<TransferInfo>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var transfer = await repository.FindAsync(id, cancellationToken);

        // A transfer that is still uploading is invisible to everyone but its owner.
        if (transfer is null || transfer.Status == TransferStatus.Uploading)
        {
            return Result<TransferInfo>.Fail(TransferError.NotFound);
        }

        if (!transfer.IsAvailable(clock.GetUtcNow()))
        {
            return Result<TransferInfo>.Fail(TransferError.Gone);
        }

        return Result<TransferInfo>.Ok(new TransferInfo(
            transfer.Id,
            transfer.CryptoHeader,
            transfer.EncryptedMetadata,
            transfer.ChunkCount,
            transfer.CiphertextBytes,
            transfer.ExpiresAt,
            transfer.DownloadsRemaining,
            transfer.PasswordProtected));
    }

    /// <summary>Counts a download against the limit and returns a token for fetching the chunks.</summary>
    public async Task<Result<DownloadGrant>> StartDownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var transfer = await repository.FindAsync(id, cancellationToken);
            if (transfer is null || transfer.Status == TransferStatus.Uploading)
            {
                return Result<DownloadGrant>.Fail(TransferError.NotFound);
            }

            var now = clock.GetUtcNow();
            if (transfer.StartDownload(now) is { } error)
            {
                return Result<DownloadGrant>.Fail(error);
            }

            repository.AddAuditEvent(AuditEvent.Create(id, AuditEventType.DownloadStarted, now));

            try
            {
                await repository.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
            {
                // Two downloads raced. Reload and re-check the limit instead of trusting stale state.
                continue;
            }
            catch (ConcurrencyConflictException)
            {
                return Result<DownloadGrant>.Fail(TransferError.Conflict, "The transfer is busy. Try again.");
            }

            var lifetime = TimeSpan.FromMinutes(options.DownloadTokenMinutes);
            return Result<DownloadGrant>.Ok(new DownloadGrant(downloadTokens.Issue(id, lifetime), now + lifetime));
        }
    }

    /// <summary>Opens one encrypted chunk for a caller holding a valid download token. The caller disposes the stream.</summary>
    public async Task<Result<Stream>> OpenChunkAsync(
        Guid id,
        string? downloadToken,
        int index,
        CancellationToken cancellationToken)
    {
        if (!downloadTokens.TryValidate(downloadToken, id))
        {
            return Result<Stream>.Fail(TransferError.Forbidden);
        }

        var transfer = await repository.FindAsync(id, cancellationToken);
        if (transfer is null)
        {
            return Result<Stream>.Fail(TransferError.NotFound);
        }

        // The download limit was already charged when the token was issued, so only status and expiry apply here.
        if (transfer.Status != TransferStatus.Ready || transfer.IsExpired(clock.GetUtcNow()))
        {
            return Result<Stream>.Fail(TransferError.Gone);
        }

        if (index < 0 || index >= transfer.ChunkCount)
        {
            return Result<Stream>.Fail(TransferError.NotFound);
        }

        var stream = await blobs.OpenReadAsync(id, index, cancellationToken);
        return stream is null ? Result<Stream>.Fail(TransferError.NotFound) : Result<Stream>.Ok(stream);
    }

    public async Task<Result> RevokeAsync(Guid id, string? ownerToken, CancellationToken cancellationToken)
    {
        var owned = await FindOwnedAsync(id, ownerToken, cancellationToken);
        if (!owned.IsSuccess)
        {
            return Result.Fail(owned.Error!.Value);
        }

        var transfer = owned.Value!;
        if (transfer.Status == TransferStatus.Revoked)
        {
            return Result.Ok();
        }

        transfer.Revoke();
        repository.AddAuditEvent(AuditEvent.Create(id, AuditEventType.Revoked, clock.GetUtcNow()));

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Result.Fail(TransferError.Conflict, "The transfer was changed by another request.");
        }

        // Delete the ciphertext only after the revocation is durable, so a failed save never loses data.
        await blobs.DeleteAllAsync(id, cancellationToken);
        return Result.Ok();
    }

    /// <summary>Deletes transfers past their expiry, with their ciphertext. Returns how many were removed.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expired = await repository.ListExpiredAsync(now, PurgeBatchSize, cancellationToken);

        foreach (var transfer in expired)
        {
            await blobs.DeleteAllAsync(transfer.Id, cancellationToken);
            repository.Remove(transfer);
            repository.AddAuditEvent(AuditEvent.Create(transfer.Id, AuditEventType.Purged, now));
        }

        if (expired.Count > 0)
        {
            await repository.SaveChangesAsync(cancellationToken);
        }

        return expired.Count;
    }

    private (TransferError Error, string Detail)? Validate(CreateTransferCommand command)
    {
        if (command.CryptoHeader.Length is 0 or > Transfer.MaxCryptoHeaderBytes)
        {
            return (TransferError.Invalid, $"cryptoHeader must be 1 to {Transfer.MaxCryptoHeaderBytes} bytes.");
        }

        if (command.EncryptedMetadata.Length is 0 or > Transfer.MaxEncryptedMetadataBytes)
        {
            return (TransferError.Invalid, $"encryptedMetadata must be 1 to {Transfer.MaxEncryptedMetadataBytes} bytes.");
        }

        if (command.ChunkCount < 1)
        {
            return (TransferError.Invalid, "chunkCount must be at least 1.");
        }

        if (command.CiphertextBytes > options.MaxCiphertextBytes)
        {
            return (TransferError.TooLarge, $"Transfers are limited to {options.MaxCiphertextBytes} bytes.");
        }

        // Every chunk holds at least a tag and at most MaxChunkBytes, which bounds the declared total both ways.
        var smallest = (long)command.ChunkCount * Transfer.MinChunkBytes;
        var largest = (long)command.ChunkCount * options.MaxChunkBytes;
        if (command.CiphertextBytes < smallest || command.CiphertextBytes > largest)
        {
            return (TransferError.Invalid, "ciphertextBytes does not fit chunkCount.");
        }

        if (command.ExpiresInHours < 1 || command.ExpiresInHours > options.MaxExpiryHours)
        {
            return (TransferError.Invalid, $"expiresInHours must be 1 to {options.MaxExpiryHours}.");
        }

        if (command.MaxDownloads is { } limit && (limit < 1 || limit > options.MaxDownloadLimit))
        {
            return (TransferError.Invalid, $"maxDownloads must be 1 to {options.MaxDownloadLimit}, or omitted.");
        }

        return null;
    }

    private async Task<Result<Transfer>> FindOwnedAsync(Guid id, string? ownerToken, CancellationToken cancellationToken)
    {
        var transfer = await repository.FindAsync(id, cancellationToken);
        if (transfer is null)
        {
            return Result<Transfer>.Fail(TransferError.NotFound);
        }

        return TryHashOwnerToken(ownerToken, out var hash) && transfer.OwnerTokenHashMatches(hash)
            ? Result<Transfer>.Ok(transfer)
            : Result<Transfer>.Fail(TransferError.Forbidden);
    }

    private static bool TryHashOwnerToken(string? ownerToken, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(ownerToken) || ownerToken.Length > 64)
        {
            return false;
        }

        // TryDecodeFromChars throws on characters outside the alphabet, so validate first.
        if (!Base64Url.IsValid(ownerToken))
        {
            return false;
        }

        Span<byte> token = stackalloc byte[OwnerTokenBytes + 16];
        if (!Base64Url.TryDecodeFromChars(ownerToken, token, out var written) || written != OwnerTokenBytes)
        {
            return false;
        }

        hash = SHA256.HashData(token[..written]);
        return true;
    }

    private static string DescribeChunkRefusal(TransferError refusal, Transfer transfer) => refusal switch
    {
        TransferError.Conflict => $"Chunks must arrive in order. Expected chunk {transfer.UploadedChunks}.",
        TransferError.TooLarge => "The transfer already holds all the bytes it declared.",
        TransferError.Gone => "The transfer has expired.",
        _ => "The transfer is not accepting uploads.",
    };
}
