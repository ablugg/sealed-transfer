using System.Buffers.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Api.Transfers;

public static class TransferEndpoints
{
    public const string OwnerTokenHeader = "X-Owner-Token";
    public const string DownloadTokenHeader = "X-Download-Token";
    public const string CreateRateLimitPolicy = "create-transfer";

    private const string OctetStream = "application/octet-stream";

    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/transfers").WithTags("Transfers");

        group.MapPost("/", CreateAsync).RequireRateLimiting(CreateRateLimitPolicy);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}/chunks/{index:int}", UploadChunkAsync).Accepts<byte[]>(OctetStream);
        group.MapPost("/{id:guid}/complete", CompleteAsync);
        group.MapPost("/{id:guid}/downloads", StartDownloadAsync);
        group.MapGet("/{id:guid}/chunks/{index:int}", DownloadChunkAsync);
        group.MapDelete("/{id:guid}", RevokeAsync);

        return routes;
    }

    private static async Task<IResult> CreateAsync(
        CreateTransferRequest request,
        HttpContext httpContext,
        TransferService service,
        CancellationToken cancellationToken)
    {
        if (!TryDecode(request.CryptoHeader, Transfer.MaxCryptoHeaderBytes, out var header)
            || !TryDecode(request.EncryptedMetadata, Transfer.MaxEncryptedMetadataBytes, out var metadata))
        {
            return Problem(TransferError.Invalid, "cryptoHeader and encryptedMetadata must be base64url within the size limits.");
        }

        // Link the transfer to the signed-in user if authenticated.
        Guid? userId = null;
        var sub = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? httpContext.User.FindFirstValue("sub");
        if (!string.IsNullOrEmpty(sub))
        {
            // Auth0 sub is "provider|id" — derive a stable GUID the same way IdentityEndpoints does.
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sub));
            userId = new Guid(hash.AsSpan(0, 16));
        }

        var result = await service.CreateAsync(
            new CreateTransferCommand(
                header,
                metadata,
                request.ChunkCount,
                request.CiphertextBytes,
                request.ExpiresInHours,
                request.MaxDownloads,
                request.PasswordProtected,
                userId),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return Problem(result.Error!.Value, result.Detail);
        }

        var created = result.Value!;
        return Results.Created(
            $"/api/transfers/{created.Id}",
            new CreateTransferResponse(created.Id, created.OwnerToken, created.ExpiresAt));
    }

    private static async Task<IResult> GetAsync(Guid id, TransferService service, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return Problem(result.Error!.Value, result.Detail);
        }

        var info = result.Value!;
        return Results.Ok(new TransferInfoResponse(
            info.Id,
            Base64Url.EncodeToString(info.CryptoHeader),
            Base64Url.EncodeToString(info.EncryptedMetadata),
            info.ChunkCount,
            info.CiphertextBytes,
            info.ExpiresAt,
            info.DownloadsRemaining,
            info.PasswordProtected));
    }

    private static async Task<IResult> UploadChunkAsync(
        Guid id,
        int index,
        [FromHeader(Name = OwnerTokenHeader)] string? ownerToken,
        HttpRequest request,
        TransferService service,
        CancellationToken cancellationToken)
    {
        // The body is streamed straight to storage. The service caps how much it will read.
        var result = await service.UploadChunkAsync(id, ownerToken, index, request.Body, cancellationToken);
        return result.IsSuccess ? Results.NoContent() : Problem(result.Error!.Value, result.Detail);
    }

    private static async Task<IResult> CompleteAsync(
        Guid id,
        [FromHeader(Name = OwnerTokenHeader)] string? ownerToken,
        TransferService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CompleteAsync(id, ownerToken, cancellationToken);
        return result.IsSuccess ? Results.NoContent() : Problem(result.Error!.Value, result.Detail);
    }

    private static async Task<IResult> StartDownloadAsync(Guid id, TransferService service, CancellationToken cancellationToken)
    {
        var result = await service.StartDownloadAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return Problem(result.Error!.Value, result.Detail);
        }

        var grant = result.Value!;
        return Results.Ok(new DownloadGrantResponse(grant.Token, grant.ExpiresAt));
    }

    private static async Task<IResult> DownloadChunkAsync(
        Guid id,
        int index,
        [FromHeader(Name = DownloadTokenHeader)] string? downloadToken,
        TransferService service,
        CancellationToken cancellationToken)
    {
        var result = await service.OpenChunkAsync(id, downloadToken, index, cancellationToken);

        // Results.Stream disposes the stream once the response has been written.
        return result.IsSuccess
            ? Results.Stream(result.Value!, OctetStream)
            : Problem(result.Error!.Value, result.Detail);
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        [FromHeader(Name = OwnerTokenHeader)] string? ownerToken,
        TransferService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RevokeAsync(id, ownerToken, cancellationToken);
        return result.IsSuccess ? Results.NoContent() : Problem(result.Error!.Value, result.Detail);
    }

    private static bool TryDecode(string? text, int maxBytes, out byte[] bytes)
    {
        bytes = [];

        // Four base64 characters carry three bytes, so this bounds the work before any decoding happens.
        // TryDecodeFromChars throws on characters outside the alphabet, so IsValid runs first.
        if (string.IsNullOrEmpty(text) || text.Length > ((maxBytes + 2) / 3 * 4) || !Base64Url.IsValid(text))
        {
            return false;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(text.Length)];
        if (!Base64Url.TryDecodeFromChars(text, buffer, out var written) || written == 0 || written > maxBytes)
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }

    private static IResult Problem(TransferError error, string? detail) => Results.Problem(
        detail: detail,
        statusCode: error switch
        {
            TransferError.NotFound => StatusCodes.Status404NotFound,
            TransferError.Forbidden => StatusCodes.Status403Forbidden,
            TransferError.Gone => StatusCodes.Status410Gone,
            TransferError.InvalidState => StatusCodes.Status409Conflict,
            TransferError.Conflict => StatusCodes.Status409Conflict,
            TransferError.Invalid => StatusCodes.Status400BadRequest,
            TransferError.TooLarge => StatusCodes.Status413PayloadTooLarge,
            _ => StatusCodes.Status500InternalServerError,
        });
}
