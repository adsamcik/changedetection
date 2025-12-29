using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that processes the notification outbox.
/// Runs periodically to send pending notifications and retry failed ones.
/// </summary>
public class NotificationOutboxProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationOutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan ProcessingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan CleanupAge = TimeSpan.FromDays(7);

    private DateTime _lastCleanup = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification outbox processor starting");

        // Recover any stale entries from previous crash on startup
        await RecoverStaleEntriesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxAsync(stoppingToken);
                await PeriodicCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in notification outbox processor");
            }

            try
            {
                await Task.Delay(ProcessingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Notification outbox processor stopped");
    }

    private async Task RecoverStaleEntriesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var outboxService = scope.ServiceProvider.GetRequiredService<INotificationOutboxService>();
            
            var recovered = await outboxService.RecoverStaleAsync(ct);
            if (recovered > 0)
            {
                logger.LogWarning(
                    "Recovered {Count} stale notifications from previous run",
                    recovered);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover stale notifications on startup");
        }
    }

    private async Task ProcessOutboxAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var outboxService = scope.ServiceProvider.GetRequiredService<INotificationOutboxService>();

        // Process pending notifications
        var pending = await outboxService.ProcessPendingAsync(50, ct);
        
        // Process retries
        var retried = await outboxService.ProcessRetryAsync(20, ct);

        if (pending > 0 || retried > 0)
        {
            logger.LogDebug(
                "Outbox processing: {Pending} pending, {Retried} retried",
                pending, retried);
        }
    }

    private async Task PeriodicCleanupAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastCleanup < CleanupInterval)
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var outboxService = scope.ServiceProvider.GetRequiredService<INotificationOutboxService>();

            var deleted = await outboxService.CleanupOldNotificationsAsync(CleanupAge, ct);
            if (deleted > 0)
            {
                logger.LogInformation(
                    "Cleaned up {Count} old sent notifications",
                    deleted);
            }

            _lastCleanup = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cleanup old notifications");
        }
    }
}
