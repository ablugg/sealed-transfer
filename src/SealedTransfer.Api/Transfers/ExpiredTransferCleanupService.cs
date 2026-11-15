using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Api.Transfers;

/// <summary>Periodically deletes expired transfers and their ciphertext.</summary>
public sealed class ExpiredTransferCleanupService(
    IServiceScopeFactory scopeFactory,
    TransferOptions options,
    TimeProvider clock,
    ILogger<ExpiredTransferCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.CleanupIntervalMinutes));
        using var timer = new PeriodicTimer(interval, clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // TransferService is scoped (it holds a DbContext), so each run gets its own scope.
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<TransferService>();
                var purged = await service.PurgeExpiredAsync(stoppingToken);

                if (purged > 0)
                {
                    logger.LogInformation("Purged {Count} expired transfers.", purged);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One failed run must not stop the job; the next tick tries again.
                logger.LogError(exception, "Expired transfer cleanup failed.");
            }
        }
    }
}
