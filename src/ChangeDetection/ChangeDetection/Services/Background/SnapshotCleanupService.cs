using ChangeDetection;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that periodically deletes snapshots older than the configured retention period.
/// Supports per-watch retention overrides with a hard ceiling, and cleans up old change events.
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
                await CleanupOldChangeEventsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
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
        var globalRetention = settings?.SnapshotRetentionDays ?? 30;
        var maxRetention = settings?.MaxRetentionDays ?? 0;

        if (globalRetention <= 0)
        {
            logger.LogDebug("Snapshot retention is disabled (SnapshotRetentionDays={Days})", globalRetention);
            return;
        }

        // Try to get per-watch retention map (null-safe for backward compat)
        var watchRepo = scope.ServiceProvider.GetService(typeof(IRepository<WatchedSite>)) as IRepository<WatchedSite>;
        Dictionary<Guid, int>? perWatchRetention = null;
        if (watchRepo is not null)
        {
            var watches = await watchRepo.GetAllAsync(ct);
            perWatchRetention = watches
                .Where(w => w.RetentionDays.HasValue)
                .ToDictionary(w => w.Id, w => w.RetentionDays!.Value);
        }

        var cutoff = DateTime.UtcNow.AddDays(-globalRetention);

        var oldSnapshots = await snapshotRepo.FindAsync(s => s.CapturedAt < cutoff, ct);
        var snapshotList = oldSnapshots.ToList();

        // If we have per-watch retention, re-evaluate each snapshot
        if (perWatchRetention is { Count: > 0 })
        {
            snapshotList = snapshotList.Where(s =>
            {
                var effective = GetEffectiveRetentionDays(
                    perWatchRetention.TryGetValue(s.WatchedSiteId, out var pw) ? pw : null,
                    globalRetention, maxRetention);
                return s.CapturedAt < DateTime.UtcNow.AddDays(-effective);
            }).ToList();
        }

        // Also enforce hard ceiling if set
        if (maxRetention > 0)
        {
            var hardCutoff = DateTime.UtcNow.AddDays(-maxRetention);
            var hardCeilingSnapshots = await snapshotRepo.FindAsync(s => s.CapturedAt < hardCutoff, ct);
            var additional = hardCeilingSnapshots.Where(s => !snapshotList.Any(x => x.Id == s.Id));
            snapshotList.AddRange(additional);
        }

        if (snapshotList.Count == 0)
            return;

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
            globalRetention);
    }

    private async Task CleanupOldChangeEventsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateBackgroundScope();

        var settingsRepo = scope.ServiceProvider.GetRequiredService<IRepository<AppSettings>>();
        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault();
        var retentionDays = settings?.ChangeEventRetentionDays ?? 90;

        if (retentionDays <= 0) return;

        var eventRepo = scope.ServiceProvider.GetService(typeof(IRepository<ChangeEvent>)) as IRepository<ChangeEvent>;
        if (eventRepo is null) return;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var oldEvents = await eventRepo.FindAsync(e => e.DetectedAt < cutoff, ct);
        var count = oldEvents.Count();

        if (count > 0)
        {
            await eventRepo.DeleteManyAsync(e => e.DetectedAt < cutoff, ct);
            logger.LogInformation("Change event cleanup: deleted {Count} events older than {Days} days", count, retentionDays);
        }
    }

    /// <summary>
    /// Calculates effective retention days considering per-watch override and hard ceiling.
    /// </summary>
    internal static int GetEffectiveRetentionDays(int? perWatch, int globalDefault, int maxCeiling)
    {
        var effective = perWatch ?? globalDefault;
        if (maxCeiling > 0 && effective > maxCeiling)
            effective = maxCeiling;
        return effective;
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
