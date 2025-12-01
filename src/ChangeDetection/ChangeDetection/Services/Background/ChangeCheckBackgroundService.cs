using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that periodically checks watches for changes.
/// </summary>
public class ChangeCheckBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ChangeCheckBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public ChangeCheckBackgroundService(
        IServiceProvider services,
        ILogger<ChangeCheckBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Change check background service starting...");

        using var timer = new PeriodicTimer(_checkInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckPendingWatchesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in change check background service");
            }
        }
    }

    private async Task CheckPendingWatchesAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var watchService = scope.ServiceProvider.GetRequiredService<IWatchService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChangeDetectionHub>>();

        var pendingWatches = await watchService.GetWatchesDueForCheckAsync(ct);
        var watchList = pendingWatches.ToList();

        if (watchList.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Checking {Count} watches for changes", watchList.Count);

        foreach (var watch in watchList)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var changeEvent = await watchService.CheckForChangesAsync(watch.Id, ct);

                if (changeEvent != null)
                {
                    // Notify via SignalR
                    await hubContext.Clients.All.SendAsync("ChangeDetected", new
                    {
                        WatchId = watch.Id,
                        WatchName = watch.Name ?? watch.Url,
                        ChangeId = changeEvent.Id,
                        Summary = changeEvent.DiffSummary,
                        DetectedAt = changeEvent.DetectedAt,
                        Importance = changeEvent.Importance.ToString()
                    }, ct);

                    // Send notifications if configured
                    var updatedWatch = await watchService.GetByIdAsync(watch.Id, ct);
                    if (updatedWatch != null && ShouldNotify(updatedWatch.Notifications, changeEvent))
                    {
                        try
                        {
                            await notificationService.SendNotificationAsync(updatedWatch, changeEvent, ct: ct);
                            
                            // Update change event as notified
                            changeEvent.IsNotified = true;
                            changeEvent.NotifiedAt = DateTime.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to send notification for watch {WatchId}", watch.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking watch {WatchId} ({Url})", watch.Id, watch.Url);
            }
        }
    }

    private static bool ShouldNotify(NotificationSettings settings, ChangeEvent change)
    {
        // Check if any notification channel is enabled
        var hasChannel = settings.EmailEnabled || settings.WebhookEnabled || settings.DiscordEnabled;
        
        // Check if change importance meets threshold
        var meetsThreshold = change.Importance >= settings.MinimumImportance;

        return hasChannel && meetsThreshold;
    }
}
