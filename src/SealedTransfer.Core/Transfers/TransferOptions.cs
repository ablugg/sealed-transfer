namespace SealedTransfer.Core.Transfers;

/// <summary>Server-side limits. Bound from the "Transfers" configuration section.</summary>
public sealed class TransferOptions
{
    public const string SectionName = "Transfers";

    /// <summary>Largest total ciphertext accepted: 200 MiB of plaintext plus room for the per-chunk tags.</summary>
    public long MaxCiphertextBytes { get; set; } = 201L * 1024 * 1024;

    /// <summary>Largest single chunk accepted: 4 MiB of plaintext plus the 16-byte AES-GCM tag.</summary>
    public int MaxChunkBytes { get; set; } = (4 * 1024 * 1024) + 16;

    public int MaxExpiryHours { get; set; } = 168;

    public int MaxDownloadLimit { get; set; } = 100;

    public int DownloadTokenMinutes { get; set; } = 15;

    public int CleanupIntervalMinutes { get; set; } = 10;
}
