using ChangeDetection;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that periodically deletes snapshots older than the configured retention period.
/// Also removes associated screenshot files from disk.
/// </summary>
public class SnapshotCleanupService(
    IBackgroundServiceScopeFactory scopeFactory,
    ILogger<SnapshotCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = ServiceConstants.SnapshotCleanupInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Snapshot cleanup service starting...");

        using var timer = new PeriodicTimer(CleanupInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CleanupOldSnapshotsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in snapshot cleanup service");
            }
        }
    }

    private async Task CleanupOldSnapshotsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateBackgroundScope();

        var settingsRepo = scope.ServiceProvider.GetRequiredService<IRepository<AppSettings>>();
        var snapshotRepo = scope.ServiceProvider.GetRequiredService<IRepository<ChangeSnapshot>>();

        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault();
        var retentionDays = settings?.SnapshotRetentionDays ?? 30;

        if (retentionDays <= 0)
        {
            logger.LogDebug("Snapshot retention is disabled (SnapshotRetentionDays={Days})", retentionDays);
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        var oldSnapshots = await snapshotRepo.FindAsync(s => s.CapturedAt < cutoff, ct);
        var snapshotList = oldSnapshots.ToList();

        if (snapshotList.Count == 0)
        {
            return;
        }

        var deletedScreenshots = 0;

        foreach (var snapshot in snapshotList)
        {
            deletedScreenshots += DeleteFileIfExists(snapshot.ScreenshotPath);
            deletedScreenshots += DeleteFileIfExists(snapshot.ElementScreenshotPath);
        }

        await snapshotRepo.DeleteManyAsync(s => s.CapturedAt < cutoff, ct);

        logger.LogInformation(
            "Snapshot cleanup completed: deleted {SnapshotCount} snapshots and {ScreenshotCount} screenshot files older than {RetentionDays} days",
            snapshotList.Count,
            deletedScreenshots,
            retentionDays);
    }

    private int DeleteFileIfExists(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return 0;

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return 1;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete screenshot file: {Path}", filePath);
        }

        return 0;
    }
}
