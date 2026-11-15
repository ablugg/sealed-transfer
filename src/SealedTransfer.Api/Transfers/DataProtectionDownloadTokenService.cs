using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Api.Transfers;

/// <summary>
/// Download tokens are the transfer id, encrypted and signed by ASP.NET Core Data Protection with a
/// built-in expiry. Nothing is stored server-side, and a token for one transfer is useless for another.
/// </summary>
public sealed class DataProtectionDownloadTokenService(IDataProtectionProvider provider) : IDownloadTokenService
{
    private readonly ITimeLimitedDataProtector _protector = provider
        .CreateProtector("SealedTransfer.DownloadToken.v1")
        .ToTimeLimitedDataProtector();

    public string Issue(Guid transferId, TimeSpan lifetime) =>
        _protector.Protect(transferId.ToString("N"), lifetime);

    public bool TryValidate(string? token, Guid transferId)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 1024)
        {
            return false;
        }

        try
        {
            return _protector.Unprotect(token) == transferId.ToString("N");
        }
        catch (CryptographicException)
        {
            // Tampered, expired, or issued under a different key.
            return false;
        }
    }
}
