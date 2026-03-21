using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Startup;

/// <summary>
/// Handles graceful shutdown by logging critical state and resetting watches that were mid-check.
/// This service runs last during shutdown (registered first) to capture final state.
/// </summary>
public class GracefulShutdownService(
    IServiceProvider serviceProvider,
    ILogger<GracefulShutdownService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Graceful shutdown service initialized");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Application shutdown initiated, saving critical state...");

        try
        {
            await ResetInProgressWatchesAsync(cancellationToken);
            
            logger.LogInformation("Graceful shutdown complete");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during graceful shutdown");
            // Don't rethrow - allow shutdown to continue
        }
    }

    /// <summary>
    /// Resets watches that are currently being checked back to Active status.
    /// This prevents them from being stuck after restart.
    /// </summary>
    private async Task ResetInProgressWatchesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var watchRepo = scope.ServiceProvider.GetRequiredService<IRepository<WatchedSite>>();

            var checkingWatches = await watchRepo.FindAsync(
                w => w.Status == WatchStatus.Checking,
                cancellationToken);

            var checkingList = checkingWatches.ToList();

            if (checkingList.Count == 0)
            {
                logger.LogDebug("No watches currently in Checking status");
                return;
            }

            logger.LogWarning(
                "Resetting {Count} watches from Checking to Active before shutdown",
                checkingList.Count);

            foreach (var watch in checkingList)
            {
                watch.Status = WatchStatus.Active;
                // Add a note that the check was interrupted
                watch.LastError = "Check interrupted by application shutdown";
                await watchRepo.UpdateAsync(watch, cancellationToken);

                logger.LogInformation(
                    "Reset watch '{Name}' ({Id}) from Checking to Active",
                    watch.Name ?? watch.Url,
                    watch.Id);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Watch reset interrupted by shutdown timeout");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset in-progress watches during shutdown");
        }
    }
}
