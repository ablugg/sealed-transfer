namespace SealedTransfer.Core.Transfers;

public sealed record CreateTransferCommand(
    byte[] CryptoHeader,
    byte[] EncryptedMetadata,
    int ChunkCount,
    long CiphertextBytes,
    int ExpiresInHours,
    int? MaxDownloads,
    bool PasswordProtected = false,
    Guid? UserId = null);

/// <summary>Returned once, when the transfer is created. The owner token cannot be recovered later.</summary>
public sealed record CreatedTransfer(Guid Id, string OwnerToken, DateTimeOffset ExpiresAt);

/// <summary>What anyone holding the transfer id may see. Nothing here is secret.</summary>
public sealed record TransferInfo(
    Guid Id,
    byte[] CryptoHeader,
    byte[] EncryptedMetadata,
    int ChunkCount,
    long CiphertextBytes,
    DateTimeOffset ExpiresAt,
    int? DownloadsRemaining,
    bool PasswordProtected);

public sealed record DownloadGrant(string Token, DateTimeOffset ExpiresAt);
