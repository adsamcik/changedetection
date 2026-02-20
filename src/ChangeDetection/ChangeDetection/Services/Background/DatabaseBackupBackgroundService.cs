using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that periodically creates database backups and cleans up old ones.
/// </summary>
public class DatabaseBackupBackgroundService(
    IDatabaseBackupService backupService,
    ILogger<DatabaseBackupBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan BackupInterval = ServiceConstants.DatabaseBackupInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Database backup service starting...");

        using var timer = new PeriodicTimer(BackupInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var path = await backupService.CreateBackupAsync(stoppingToken);
                logger.LogInformation("Scheduled backup created: {Path}", path);

                var deleted = await backupService.CleanupOldBackupsAsync(
                    ServiceConstants.DatabaseBackupRetainCount, stoppingToken);

                if (deleted > 0)
                    logger.LogInformation("Cleaned up {DeletedCount} old backups", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in database backup service");
            }
        }
    }
}
