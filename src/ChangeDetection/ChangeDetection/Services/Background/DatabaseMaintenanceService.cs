using ChangeDetection.Services.Persistence;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that periodically compacts the LiteDB database to reclaim space
/// and monitors database size for health warnings.
/// </summary>
public class DatabaseMaintenanceService(
    LiteDbContext dbContext,
    IConfiguration configuration,
    ILogger<DatabaseMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = ServiceConstants.DatabaseMaintenanceInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Database maintenance service starting (interval: {Interval})", MaintenanceInterval);

        using var timer = new PeriodicTimer(MaintenanceInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var dbPath = GetDatabasePath();
                var sizeBefore = GetFileSizeBytes(dbPath);

                logger.LogInformation("Starting database compaction (current size: {SizeMb:F1} MB)",
                    sizeBefore / (1024.0 * 1024.0));

                dbContext.Database.Checkpoint();
                var rebuiltSize = dbContext.Database.Rebuild();

                var sizeAfter = GetFileSizeBytes(dbPath);
                var savedBytes = sizeBefore - sizeAfter;
                var savedPct = sizeBefore > 0 ? (double)savedBytes / sizeBefore * 100 : 0;

                logger.LogInformation(
                    "Database compaction complete: {BeforeMb:F1} MB → {AfterMb:F1} MB (saved {SavedPct:F1}%, {RebuiltPages} pages)",
                    sizeBefore / (1024.0 * 1024.0),
                    sizeAfter / (1024.0 * 1024.0),
                    savedPct,
                    rebuiltSize);

                CheckSizeWarning(sizeAfter);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during database maintenance");
            }
        }
    }

    private void CheckSizeWarning(long sizeBytes)
    {
        var sizeMb = sizeBytes / (1024.0 * 1024.0);
        if (sizeMb > ServiceConstants.DatabaseSizeWarningMb)
        {
            logger.LogWarning(
                "Database size ({SizeMb:F0} MB) exceeds warning threshold ({ThresholdMb} MB). " +
                "Consider reducing retention settings or archiving old data.",
                sizeMb, ServiceConstants.DatabaseSizeWarningMb);
        }
    }

    /// <summary>
    /// Returns current database health metrics for the monitoring endpoint.
    /// </summary>
    internal DatabaseHealthInfo GetHealthInfo()
    {
        var dbPath = GetDatabasePath();
        var sizeBytes = GetFileSizeBytes(dbPath);
        var sizeMb = sizeBytes / (1024.0 * 1024.0);

        var collectionStats = new List<CollectionStat>();
        foreach (var name in dbContext.Database.GetCollectionNames())
        {
            var col = dbContext.Database.GetCollection(name);
            collectionStats.Add(new CollectionStat(name, col.Count()));
        }

        return new DatabaseHealthInfo(
            FilePath: dbPath,
            SizeBytes: sizeBytes,
            SizeMb: Math.Round(sizeMb, 1),
            WarningThresholdMb: ServiceConstants.DatabaseSizeWarningMb,
            IsOverWarningThreshold: sizeMb > ServiceConstants.DatabaseSizeWarningMb,
            CompactionIntervalDays: MaintenanceInterval.TotalDays,
            Collections: collectionStats);
    }

    private string GetDatabasePath() =>
        configuration.GetValue<string>("LiteDb:Path") ?? "changedetection.db";

    internal static long GetFileSizeBytes(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}

public record DatabaseHealthInfo(
    string FilePath,
    long SizeBytes,
    double SizeMb,
    long WarningThresholdMb,
    bool IsOverWarningThreshold,
    double CompactionIntervalDays,
    List<CollectionStat> Collections);

public record CollectionStat(string Name, int DocumentCount);
