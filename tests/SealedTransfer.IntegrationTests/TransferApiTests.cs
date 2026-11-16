using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SealedTransfer.Api.Transfers;

namespace SealedTransfer.IntegrationTests;

public sealed class TransferApiTests : IClassFixture<SealedTransferFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _client;

    public TransferApiTests(SealedTransferFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static CreateTransferRequest ValidRequest() => new(
        CryptoHeader: Convert.ToBase64String(new byte[12]).TrimEnd('='),
        EncryptedMetadata: Convert.ToBase64String(new byte[40]).TrimEnd('='),
        ChunkCount: 1,
        CiphertextBytes: 32,
        ExpiresInHours: 24,
        MaxDownloads: null);

    private async Task<(Guid Id, string OwnerToken)> CreateTransferAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/transfers", ValidRequest());
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CreateTransferResponse>(JsonOptions);
        return (body!.Id, body.OwnerToken);
    }

    private async Task UploadAndCompleteAsync(Guid id, string ownerToken, byte[] chunk)
    {
        var uploadRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/transfers/{id}/chunks/0");
        uploadRequest.Headers.Add(TransferEndpoints.OwnerTokenHeader, ownerToken);
        uploadRequest.Content = new ByteArrayContent(chunk);
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var uploadResponse = await _client.SendAsync(uploadRequest);
        Assert.Equal(HttpStatusCode.NoContent, uploadResponse.StatusCode);

        var completeRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/transfers/{id}/complete");
        completeRequest.Headers.Add(TransferEndpoints.OwnerTokenHeader, ownerToken);
        var completeResponse = await _client.SendAsync(completeRequest);
        Assert.Equal(HttpStatusCode.NoContent, completeResponse.StatusCode);
    }

    [Fact]
    public async Task Create_returns_201_with_id_and_owner_token()
    {
        var response = await _client.PostAsJsonAsync("/api/transfers", ValidRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateTransferResponse>(JsonOptions);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.False(string.IsNullOrEmpty(body.OwnerToken));
    }

    [Fact]
    public async Task Create_with_invalid_body_returns_400()
    {
        var bad = ValidRequest() with { ChunkCount = 0 };
        var response = await _client.PostAsJsonAsync("/api/transfers", bad);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_nonexistent_transfer_returns_404()
    {
        var response = await _client.GetAsync($"/api/transfers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_uploading_transfer_returns_404()
    {
        var (id, _) = await CreateTransferAsync();

        var response = await _client.GetAsync($"/api/transfers/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Full_upload_then_download_round_trip()
    {
        var chunk = new byte[32];
        Random.Shared.NextBytes(chunk);

        var (id, ownerToken) = await CreateTransferAsync();
        await UploadAndCompleteAsync(id, ownerToken, chunk);

        // GET transfer info
        var infoResponse = await _client.GetAsync($"/api/transfers/{id}");
        Assert.Equal(HttpStatusCode.OK, infoResponse.StatusCode);
        var info = await infoResponse.Content.ReadFromJsonAsync<TransferInfoResponse>(JsonOptions);
        Assert.Equal(1, info!.ChunkCount);

        // Start download
        var grantResponse = await _client.PostAsync($"/api/transfers/{id}/downloads", null);
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);
        var grant = await grantResponse.Content.ReadFromJsonAsync<DownloadGrantResponse>(JsonOptions);
        Assert.False(string.IsNullOrEmpty(grant!.DownloadToken));

        // Download chunk
        var downloadRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/transfers/{id}/chunks/0");
        downloadRequest.Headers.Add(TransferEndpoints.DownloadTokenHeader, grant.DownloadToken);
        var downloadResponse = await _client.SendAsync(downloadRequest);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        var downloaded = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(chunk, downloaded);
    }

    [Fact]
    public async Task Upload_with_wrong_owner_token_returns_403()
    {
        var (id, _) = await CreateTransferAsync();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/transfers/{id}/chunks/0");
        request.Headers.Add(TransferEndpoints.OwnerTokenHeader, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        request.Content = new ByteArrayContent(new byte[32]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Download_without_token_returns_403()
    {
        var chunk = new byte[32];
        var (id, ownerToken) = await CreateTransferAsync();
        await UploadAndCompleteAsync(id, ownerToken, chunk);

        var response = await _client.GetAsync($"/api/transfers/{id}/chunks/0");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Download_limit_enforced()
    {
        var request = ValidRequest() with { MaxDownloads = 1 };
        var response = await _client.PostAsJsonAsync("/api/transfers", request);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateTransferResponse>(JsonOptions);

        await UploadAndCompleteAsync(created!.Id, created.OwnerToken, new byte[32]);

        // First download succeeds
        var grant1 = await _client.PostAsync($"/api/transfers/{created.Id}/downloads", null);
        Assert.Equal(HttpStatusCode.OK, grant1.StatusCode);

        // Second download fails — limit reached
        var grant2 = await _client.PostAsync($"/api/transfers/{created.Id}/downloads", null);
        Assert.Equal(HttpStatusCode.Gone, grant2.StatusCode);
    }

    [Fact]
    public async Task Revoke_deletes_transfer_and_blocks_further_access()
    {
        var (id, ownerToken) = await CreateTransferAsync();
        await UploadAndCompleteAsync(id, ownerToken, new byte[32]);

        var revokeRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/transfers/{id}");
        revokeRequest.Headers.Add(TransferEndpoints.OwnerTokenHeader, ownerToken);
        var revokeResponse = await _client.SendAsync(revokeRequest);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        // Transfer is now gone
        var getResponse = await _client.GetAsync($"/api/transfers/{id}");
        Assert.Equal(HttpStatusCode.Gone, getResponse.StatusCode);
    }

    [Fact]
    public async Task Security_headers_present_on_api_response()
    {
        var response = await _client.GetAsync($"/api/transfers/{Guid.NewGuid()}");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}
