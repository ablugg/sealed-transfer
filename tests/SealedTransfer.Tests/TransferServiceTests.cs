using System.Buffers.Text;
using System.Security.Cryptography;
using SealedTransfer.Core.Transfers;
using SealedTransfer.Tests.Fakes;

namespace SealedTransfer.Tests;

public class TransferServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryTransferRepository _repository = new();
    private readonly InMemoryBlobStore _blobs = new();
    private readonly TransferOptions _options = new();
    private readonly TransferService _service;

    public TransferServiceTests()
    {
        _service = new TransferService(_repository, _blobs, new FakeDownloadTokenService(), _options, _clock);
    }

    private static CreateTransferCommand Command(
        int chunkCount = 2,
        long ciphertextBytes = 64,
        int expiresInHours = 24,
        int? maxDownloads = null) =>
        new(new byte[12], new byte[40], chunkCount, ciphertextBytes, expiresInHours, maxDownloads);

    private static byte[] Chunk(int length) => RandomNumberGenerator.GetBytes(length);

    /// <summary>Creates a transfer, uploads the given chunks and completes it.</summary>
    private async Task<(CreatedTransfer Created, byte[][] Chunks)> ReadyTransferAsync(int? maxDownloads = null)
    {
        byte[][] chunks = [Chunk(32), Chunk(32)];
        var created = (await _service.CreateAsync(Command(maxDownloads: maxDownloads), None)).Value!;

        for (var index = 0; index < chunks.Length; index++)
        {
            var upload = await _service.UploadChunkAsync(created.Id, created.OwnerToken, index, new MemoryStream(chunks[index]), None);
            Assert.True(upload.IsSuccess);
        }

        Assert.True((await _service.CompleteAsync(created.Id, created.OwnerToken, None)).IsSuccess);
        return (created, chunks);
    }

    [Fact]
    public async Task Create_returns_a_32_byte_owner_token_and_records_an_audit_event()
    {
        var result = await _service.CreateAsync(Command(), None);

        Assert.True(result.IsSuccess);
        Assert.Equal(32, Base64Url.DecodeFromChars(result.Value!.OwnerToken).Length);
        Assert.Equal(_clock.GetUtcNow().AddHours(24), result.Value.ExpiresAt);
        Assert.Contains(_repository.AuditEvents, e => e.TransferId == result.Value.Id && e.Type == AuditEventType.Created);
    }

    [Fact]
    public async Task Create_rejects_a_transfer_over_the_size_limit()
    {
        var result = await _service.CreateAsync(
            Command(chunkCount: 100, ciphertextBytes: _options.MaxCiphertextBytes + 1), None);

        Assert.Equal(TransferError.TooLarge, result.Error);
        Assert.Equal(0, _repository.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(169)]
    public async Task Create_rejects_an_expiry_outside_the_allowed_range(int expiresInHours)
    {
        var result = await _service.CreateAsync(Command(expiresInHours: expiresInHours), None);

        Assert.Equal(TransferError.Invalid, result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Create_rejects_a_download_limit_outside_the_allowed_range(int maxDownloads)
    {
        var result = await _service.CreateAsync(Command(maxDownloads: maxDownloads), None);

        Assert.Equal(TransferError.Invalid, result.Error);
    }

    [Fact]
    public async Task Create_rejects_a_declared_size_that_cannot_fit_the_chunk_count()
    {
        var result = await _service.CreateAsync(Command(chunkCount: 4, ciphertextBytes: 63), None);

        Assert.Equal(TransferError.Invalid, result.Error);
    }

    [Fact]
    public async Task Upload_with_the_wrong_owner_token_is_forbidden_and_stores_nothing()
    {
        var created = (await _service.CreateAsync(Command(), None)).Value!;
        var wrongToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        var result = await _service.UploadChunkAsync(created.Id, wrongToken, 0, new MemoryStream(Chunk(32)), None);

        Assert.Equal(TransferError.Forbidden, result.Error);
        Assert.Equal(0, _blobs.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("c2hvcnQ")]
    public async Task Upload_with_a_malformed_owner_token_is_forbidden(string? ownerToken)
    {
        var created = (await _service.CreateAsync(Command(), None)).Value!;

        var result = await _service.UploadChunkAsync(created.Id, ownerToken, 0, new MemoryStream(Chunk(32)), None);

        Assert.Equal(TransferError.Forbidden, result.Error);
    }

    [Fact]
    public async Task Upload_of_more_bytes_than_declared_is_too_large()
    {
        var created = (await _service.CreateAsync(Command(chunkCount: 2, ciphertextBytes: 64), None)).Value!;

        var result = await _service.UploadChunkAsync(created.Id, created.OwnerToken, 0, new MemoryStream(Chunk(65)), None);

        Assert.Equal(TransferError.TooLarge, result.Error);
    }

    [Fact]
    public async Task Upload_out_of_order_is_a_conflict()
    {
        var created = (await _service.CreateAsync(Command(), None)).Value!;

        var result = await _service.UploadChunkAsync(created.Id, created.OwnerToken, 1, new MemoryStream(Chunk(32)), None);

        Assert.Equal(TransferError.Conflict, result.Error);
    }

    [Fact]
    public async Task A_transfer_is_invisible_until_its_upload_completes()
    {
        var created = (await _service.CreateAsync(Command(), None)).Value!;

        Assert.Equal(TransferError.NotFound, (await _service.GetAsync(created.Id, None)).Error);
        Assert.Equal(TransferError.NotFound, (await _service.StartDownloadAsync(created.Id, None)).Error);
    }

    [Fact]
    public async Task Complete_before_every_chunk_arrives_is_refused()
    {
        var created = (await _service.CreateAsync(Command(), None)).Value!;
        await _service.UploadChunkAsync(created.Id, created.OwnerToken, 0, new MemoryStream(Chunk(32)), None);

        var result = await _service.CompleteAsync(created.Id, created.OwnerToken, None);

        Assert.Equal(TransferError.InvalidState, result.Error);
    }

    [Fact]
    public async Task A_completed_transfer_downloads_the_same_bytes_that_were_uploaded()
    {
        var (created, chunks) = await ReadyTransferAsync();

        var info = (await _service.GetAsync(created.Id, None)).Value!;
        var grant = (await _service.StartDownloadAsync(created.Id, None)).Value!;

        Assert.Equal(2, info.ChunkCount);
        for (var index = 0; index < chunks.Length; index++)
        {
            var opened = await _service.OpenChunkAsync(created.Id, grant.Token, index, None);
            using var copy = new MemoryStream();
            await using (opened.Value!)
            {
                await opened.Value!.CopyToAsync(copy, None);
            }

            Assert.Equal(chunks[index], copy.ToArray());
        }

        Assert.Contains(_repository.AuditEvents, e => e.Type == AuditEventType.DownloadStarted);
    }

    [Fact]
    public async Task Downloads_beyond_the_limit_are_gone()
    {
        var (created, _) = await ReadyTransferAsync(maxDownloads: 1);

        Assert.True((await _service.StartDownloadAsync(created.Id, None)).IsSuccess);
        Assert.Equal(TransferError.Gone, (await _service.StartDownloadAsync(created.Id, None)).Error);
        Assert.Equal(TransferError.Gone, (await _service.GetAsync(created.Id, None)).Error);
    }

    [Fact]
    public async Task A_download_already_started_can_finish_after_the_limit_is_reached()
    {
        var (created, _) = await ReadyTransferAsync(maxDownloads: 1);
        var grant = (await _service.StartDownloadAsync(created.Id, None)).Value!;

        var opened = await _service.OpenChunkAsync(created.Id, grant.Token, 1, None);

        Assert.True(opened.IsSuccess);
        await opened.Value!.DisposeAsync();
    }

    [Fact]
    public async Task A_download_token_for_another_transfer_is_forbidden()
    {
        var (first, _) = await ReadyTransferAsync();
        var (second, _) = await ReadyTransferAsync();
        var grant = (await _service.StartDownloadAsync(first.Id, None)).Value!;

        var result = await _service.OpenChunkAsync(second.Id, grant.Token, 0, None);

        Assert.Equal(TransferError.Forbidden, result.Error);
    }

    [Fact]
    public async Task A_chunk_index_outside_the_transfer_is_not_found()
    {
        var (created, _) = await ReadyTransferAsync();
        var grant = (await _service.StartDownloadAsync(created.Id, None)).Value!;

        Assert.Equal(TransferError.NotFound, (await _service.OpenChunkAsync(created.Id, grant.Token, 2, None)).Error);
        Assert.Equal(TransferError.NotFound, (await _service.OpenChunkAsync(created.Id, grant.Token, -1, None)).Error);
    }

    [Fact]
    public async Task An_expired_transfer_is_gone_even_with_a_valid_download_token()
    {
        var (created, _) = await ReadyTransferAsync();
        var grant = (await _service.StartDownloadAsync(created.Id, None)).Value!;

        _clock.Advance(TimeSpan.FromHours(24));

        Assert.Equal(TransferError.Gone, (await _service.GetAsync(created.Id, None)).Error);
        Assert.Equal(TransferError.Gone, (await _service.StartDownloadAsync(created.Id, None)).Error);
        Assert.Equal(TransferError.Gone, (await _service.OpenChunkAsync(created.Id, grant.Token, 0, None)).Error);
    }

    [Fact]
    public async Task Revoke_deletes_the_ciphertext_and_blocks_downloads()
    {
        var (created, _) = await ReadyTransferAsync();

        var result = await _service.RevokeAsync(created.Id, created.OwnerToken, None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _blobs.Count);
        Assert.Equal(TransferError.Gone, (await _service.StartDownloadAsync(created.Id, None)).Error);
        Assert.Contains(_repository.AuditEvents, e => e.Type == AuditEventType.Revoked);
    }

    [Fact]
    public async Task Revoke_requires_the_owner_token()
    {
        var (created, _) = await ReadyTransferAsync();

        var result = await _service.RevokeAsync(created.Id, ownerToken: null, None);

        Assert.Equal(TransferError.Forbidden, result.Error);
        Assert.Equal(2, _blobs.Count);
    }

    [Fact]
    public async Task Purge_removes_expired_transfers_and_their_ciphertext_but_keeps_the_audit_trail()
    {
        var (expiring, _) = await ReadyTransferAsync();
        _clock.Advance(TimeSpan.FromHours(23));
        var (fresh, _) = await ReadyTransferAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        var purged = await _service.PurgeExpiredAsync(None);

        Assert.Equal(1, purged);
        Assert.Equal(1, _repository.Count);
        Assert.Equal(2, _blobs.Count);
        Assert.Equal(TransferError.NotFound, (await _service.GetAsync(expiring.Id, None)).Error);
        Assert.True((await _service.GetAsync(fresh.Id, None)).IsSuccess);
        Assert.Contains(_repository.AuditEvents, e => e.TransferId == expiring.Id && e.Type == AuditEventType.Created);
        Assert.Contains(_repository.AuditEvents, e => e.TransferId == expiring.Id && e.Type == AuditEventType.Purged);
    }
}
