using System.Security.Cryptography;
using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Tests;

public class TransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] OwnerToken = RandomNumberGenerator.GetBytes(32);

    private static Transfer NewTransfer(int chunkCount = 2, long ciphertextBytes = 64, int? maxDownloads = null) =>
        Transfer.Create(
            Guid.NewGuid(),
            cryptoHeader: new byte[12],
            encryptedMetadata: new byte[40],
            chunkCount,
            ciphertextBytes,
            SHA256.HashData(OwnerToken),
            Now,
            TimeSpan.FromHours(24),
            maxDownloads);

    private static Transfer ReadyTransfer(int? maxDownloads = null)
    {
        var transfer = NewTransfer(maxDownloads: maxDownloads);
        transfer.RecordChunk(0, 32, Now);
        transfer.RecordChunk(1, 32, Now);
        transfer.Complete(Now);
        return transfer;
    }

    [Fact]
    public void Create_starts_in_uploading_and_sets_expiry()
    {
        var transfer = NewTransfer();

        Assert.Equal(TransferStatus.Uploading, transfer.Status);
        Assert.Equal(Now.AddHours(24), transfer.ExpiresAt);
        Assert.False(transfer.IsAvailable(Now));
    }

    [Fact]
    public void Chunks_must_arrive_in_order()
    {
        var transfer = NewTransfer();

        Assert.Equal(TransferError.Conflict, transfer.RecordChunk(1, 32, Now));
        Assert.Null(transfer.RecordChunk(0, 32, Now));
        Assert.Equal(TransferError.Conflict, transfer.RecordChunk(0, 32, Now));
    }

    [Fact]
    public void A_chunk_cannot_exceed_the_declared_total()
    {
        var transfer = NewTransfer(chunkCount: 2, ciphertextBytes: 64);
        transfer.RecordChunk(0, 40, Now);

        Assert.Equal(TransferError.TooLarge, transfer.RecordChunk(1, 25, Now));
    }

    [Fact]
    public void A_chunk_smaller_than_an_authentication_tag_is_invalid()
    {
        var transfer = NewTransfer();

        Assert.Equal(TransferError.Invalid, transfer.RecordChunk(0, 15, Now));
    }

    [Fact]
    public void Complete_requires_every_chunk_and_byte()
    {
        var transfer = NewTransfer();
        transfer.RecordChunk(0, 32, Now);

        Assert.Equal(TransferError.InvalidState, transfer.Complete(Now));

        transfer.RecordChunk(1, 32, Now);

        Assert.Null(transfer.Complete(Now));
        Assert.Equal(TransferStatus.Ready, transfer.Status);
    }

    [Fact]
    public void Downloads_stop_at_the_limit()
    {
        var transfer = ReadyTransfer(maxDownloads: 2);

        Assert.Null(transfer.StartDownload(Now));
        Assert.Equal(1, transfer.DownloadsRemaining);
        Assert.Null(transfer.StartDownload(Now));
        Assert.Equal(TransferError.Gone, transfer.StartDownload(Now));
        Assert.Equal(2, transfer.DownloadCount);
    }

    [Fact]
    public void A_transfer_is_unavailable_from_its_expiry_instant()
    {
        var transfer = ReadyTransfer();

        Assert.True(transfer.IsAvailable(Now.AddHours(24).AddSeconds(-1)));
        Assert.False(transfer.IsAvailable(Now.AddHours(24)));
        Assert.Equal(TransferError.Gone, transfer.StartDownload(Now.AddHours(24)));
    }

    [Fact]
    public void A_revoked_transfer_cannot_be_downloaded()
    {
        var transfer = ReadyTransfer();
        transfer.Revoke();

        Assert.Equal(TransferError.Gone, transfer.StartDownload(Now));
    }

    [Fact]
    public void Owner_token_hash_matches_only_the_real_token()
    {
        var transfer = NewTransfer();

        Assert.True(transfer.OwnerTokenHashMatches(SHA256.HashData(OwnerToken)));
        Assert.False(transfer.OwnerTokenHashMatches(SHA256.HashData(RandomNumberGenerator.GetBytes(32))));
    }
}
