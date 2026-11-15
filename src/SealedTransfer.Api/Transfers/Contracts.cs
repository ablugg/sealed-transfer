namespace SealedTransfer.Api.Transfers;

// Byte fields travel as base64url strings. Everything in them is ciphertext or public crypto parameters.

public sealed record CreateTransferRequest(
    string? CryptoHeader,
    string? EncryptedMetadata,
    int ChunkCount,
    long CiphertextBytes,
    int ExpiresInHours,
    int? MaxDownloads,
    bool PasswordProtected = false);

public sealed record CreateTransferResponse(Guid Id, string OwnerToken, DateTimeOffset ExpiresAt);

public sealed record TransferInfoResponse(
    Guid Id,
    string CryptoHeader,
    string EncryptedMetadata,
    int ChunkCount,
    long CiphertextBytes,
    DateTimeOffset ExpiresAt,
    int? DownloadsRemaining,
    bool PasswordProtected);

public sealed record DownloadGrantResponse(string DownloadToken, DateTimeOffset ExpiresAt);
