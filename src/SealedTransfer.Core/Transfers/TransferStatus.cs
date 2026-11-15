namespace SealedTransfer.Core.Transfers;

public enum TransferStatus
{
    /// <summary>Created; the sender is still uploading chunks.</summary>
    Uploading = 0,

    /// <summary>All chunks are stored and the transfer can be downloaded.</summary>
    Ready = 1,

    /// <summary>The sender revoked it; the ciphertext has been deleted.</summary>
    Revoked = 2,
}
