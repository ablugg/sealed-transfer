using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SealedTransfer.Infrastructure.Persistence;

namespace SealedTransfer.IntegrationTests;

public sealed class SealedTransferFactory : WebApplicationFactory<Program>
{
    private readonly string _blobRoot = Path.Combine(Path.GetTempPath(), $"sealed-transfer-tests-{Guid.NewGuid():N}");
    private readonly string _dbName = $"SealedTransfer-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Storage:RootPath", _blobRoot);
        builder.UseSetting("SkipMigrations", "true");

        builder.ConfigureServices(services =>
        {
            // Remove every service descriptor that touches the real DbContext/Npgsql
            // so the InMemory provider is the only one registered.
            var descriptorsToRemove = services
                .Where(d =>
                    d.ServiceType == typeof(DbContextOptions<SealedTransferDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    (d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>)) ||
                    d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true ||
                    d.ServiceType.FullName?.Contains("Npgsql") == true ||
                    d.ImplementationType?.FullName?.Contains("Npgsql") == true)
                .ToList();
            foreach (var d in descriptorsToRemove)
            {
                services.Remove(d);
            }

            // Re-register SealedTransferDbContext with InMemory provider.
            services.AddDbContext<SealedTransferDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SealedTransferDbContext>();
        db.Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_blobRoot))
        {
            Directory.Delete(_blobRoot, recursive: true);
        }
    }
}
