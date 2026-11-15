using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SealedTransfer.Core.Transfers;
using SealedTransfer.Infrastructure.Persistence;
using SealedTransfer.Infrastructure.Storage;

namespace SealedTransfer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        string blobRootPath)
    {
        services.AddDbContext<SealedTransferDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ITransferRepository, EfTransferRepository>();
        services.AddSingleton<IBlobStore>(new FileSystemBlobStore(blobRootPath));
        return services;
    }
}
