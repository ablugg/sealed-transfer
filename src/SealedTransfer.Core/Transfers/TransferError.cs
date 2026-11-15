namespace SealedTransfer.Core.Transfers;

/// <summary>Why a transfer operation was refused. The API maps each value to an HTTP status code.</summary>
public enum TransferError
{
    /// <summary>No such transfer, or it is not visible to the caller yet.</summary>
    NotFound,

    /// <summary>The owner token or download token is missing or wrong.</summary>
    Forbidden,

    /// <summary>The transfer existed but is expired, revoked, or out of downloads.</summary>
    Gone,

    /// <summary>The operation does not fit the transfer's current status.</summary>
    InvalidState,

    /// <summary>The request itself is malformed or outside the configured limits.</summary>
    Invalid,

    /// <summary>More bytes were sent than the transfer declared or the server allows.</summary>
    TooLarge,

    /// <summary>The request raced with another one, or chunks arrived out of order.</summary>
    Conflict,
}
