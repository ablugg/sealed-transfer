using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SealedTransfer.Api.Transfers;
using SealedTransfer.Core.Transfers;
using SealedTransfer.Infrastructure;
using SealedTransfer.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TransferOptions>(builder.Configuration.GetSection(TransferOptions.SectionName));
builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<TransferOptions>>().Value);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDataProtection();
builder.Services.AddSingleton<IDownloadTokenService, DataProtectionDownloadTokenService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddHostedService<ExpiredTransferCleanupService>();

var connectionString = builder.Configuration.GetConnectionString("SealedTransfer")
    ?? throw new InvalidOperationException(
        "Connection string 'SealedTransfer' is not set. See the README for how to set it with user secrets.");
var blobRoot = builder.Configuration["Storage:RootPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "blobs");
builder.Services.AddInfrastructure(connectionString, blobRoot);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.AddPolicy(TransferEndpoints.CreateRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(10),
            }));
});

// Railway sets PORT; Kestrel reads ASPNETCORE_URLS.
var port = Environment.GetEnvironmentVariable("PORT");
if (port is not null)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var app = builder.Build();

// Auto-apply pending migrations on startup so Railway deploys just work.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SealedTransferDbContext>();
    await db.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// Security headers for API responses.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    // Don't set restrictive CSP or Cache-Control on static files.
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        headers.CacheControl = "no-store";
    }
    await next(context);
});

app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// In production, serve the Angular build output from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapTransferEndpoints();

// SPA fallback: any non-API, non-file request serves index.html so Angular routing works.
app.MapFallbackToFile("index.html");

app.Run();

// Lets integration tests start the API in-process with WebApplicationFactory<Program>.
public partial class Program;
