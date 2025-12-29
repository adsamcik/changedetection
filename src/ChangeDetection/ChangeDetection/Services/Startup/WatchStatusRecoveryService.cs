using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Startup;

/// <summary>
/// Recovers watches stuck in "Checking" status after an unexpected application restart.
/// This ensures watches don't remain in a stale state that prevents them from being checked again.
/// </summary>
public class WatchStatusRecoveryService(
    IServiceProvider serviceProvider,
    ILogger<WatchStatusRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var watchRepo = scope.ServiceProvider.GetRequiredService<IRepository<WatchedSite>>();

        try
        {
            // Find all watches stuck in Checking status
            var stuckWatches = await watchRepo.FindAsync(
                w => w.Status == WatchStatus.Checking, 
                cancellationToken);

            var stuckList = stuckWatches.ToList();

            if (stuckList.Count == 0)
            {
                logger.LogDebug("No watches stuck in Checking status");
                return;
            }

            logger.LogWarning(
                "Found {Count} watches stuck in Checking status from previous session, resetting to Active",
                stuckList.Count);

            foreach (var watch in stuckList)
            {
                watch.Status = WatchStatus.Active;
                // Don't clear LastError - it may contain useful info from before the crash
                await watchRepo.UpdateAsync(watch, cancellationToken);

                logger.LogInformation(
                    "Reset watch '{Name}' ({Id}) from Checking to Active",
                    watch.Name ?? watch.Url,
                    watch.Id);
            }

            logger.LogInformation("Watch status recovery complete, reset {Count} watches", stuckList.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover stuck watches during startup");
            // Don't rethrow - we don't want to prevent app startup due to recovery failure
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
