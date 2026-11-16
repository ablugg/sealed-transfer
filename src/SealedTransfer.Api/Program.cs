using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SealedTransfer.Api.Identity;
using SealedTransfer.Api.Transfers;
using SealedTransfer.Core.Identity;
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
builder.Services.AddScoped<UserSyncService>();
builder.Services.AddHostedService<ExpiredTransferCleanupService>();

// Auth0 authentication — enabled only when Auth0:Domain is configured.
// When disabled, all endpoints remain accessible anonymously.
var auth0Domain = builder.Configuration["Auth0:Domain"];
var auth0Audience = builder.Configuration["Auth0:Audience"];
var authConfigured = !string.IsNullOrEmpty(auth0Domain);
if (authConfigured)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"https://{auth0Domain}/";
            options.Audience = auth0Audience;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = ClaimTypes.NameIdentifier,
            };
        });
    builder.Services.AddAuthorization();
}
else
{
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization();
}

// Railway provides DATABASE_URL as a postgres:// URI. Convert it to an Npgsql connection string.
// Locally, use ConnectionStrings:SealedTransfer via user secrets.
var connectionString = builder.Configuration.GetConnectionString("SealedTransfer")
    ?? ConvertDatabaseUrl(Environment.GetEnvironmentVariable("DATABASE_URL"))
    ?? throw new InvalidOperationException(
        "Connection string 'SealedTransfer' is not set. Set it with user secrets or provide DATABASE_URL.");

static string? ConvertDatabaseUrl(string? url)
{
    if (string.IsNullOrEmpty(url)) return null;
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':');
    return $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=true";
}
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
// Integration tests set SkipMigrations=true and use EnsureCreated instead.
if (!string.Equals(app.Configuration["SkipMigrations"], "true", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SealedTransferDbContext>();
    await db.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// Security headers.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "DENY";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        headers.CacheControl = "no-store";
    }
    await next(context);
});

// Strict CSP for the SPA HTML. Static JS/CSS assets are covered by SRI hashes
// that Angular embeds in the HTML at build time, so we can use 'strict-dynamic'
// to trust scripts loaded by the inline bootstrap without listing hashes here.
// The 'unsafe-inline' is ignored by browsers that support 'strict-dynamic'.
app.Use(async (context, next) =>
{
    await next(context);

    // Only add the SPA CSP to HTML responses (index.html / fallback).
    if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; " +
            "script-src 'self' 'unsafe-inline'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self'; " +
            "img-src 'self' data:; " +
            "font-src 'self'; " +
            "base-uri 'self'; " +
            "form-action 'none'; " +
            "frame-ancestors 'none'";
    }
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// In production, serve the Angular build output from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapTransferEndpoints();
app.MapIdentityEndpoints();

// SPA fallback: any non-API, non-file request serves index.html so Angular routing works.
app.MapFallbackToFile("index.html");

app.Run();

// Lets integration tests start the API in-process with WebApplicationFactory<Program>.
public partial class Program;
