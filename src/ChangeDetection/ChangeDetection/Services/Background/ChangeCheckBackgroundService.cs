using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;
using ChangeDetection.Services.Authentication;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that periodically checks watches for changes.
/// Processes all watches across all users using BackgroundServiceUserContext.
/// </summary>
public class ChangeCheckBackgroundService : BackgroundService
{
    private readonly IBackgroundServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChangeCheckBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public ChangeCheckBackgroundService(
        IBackgroundServiceScopeFactory scopeFactory,
        ILogger<ChangeCheckBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }
    
    /// <summary>
    /// Gets the SignalR group name for a watch owner.
    /// </summary>
    private static string GetDashboardGroup(Guid ownerId)
    {
        // Single-user mode (Guid.Empty) uses global dashboard
        return ownerId == Guid.Empty 
            ? "dashboard" 
            : $"dashboard-{ownerId}";
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
        // Use background service scope to get admin-level access to all watches
        using var scope = _scopeFactory.CreateBackgroundScope();
        
        var watchService = scope.ServiceProvider.GetRequiredService<IWatchService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChangeDetectionHub>>();
        var eventRepo = scope.ServiceProvider.GetRequiredService<IRepository<ChangeEvent>>();

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
            
            // Determine which SignalR group to broadcast to based on watch owner
            var dashboardGroup = GetDashboardGroup(watch.OwnerId);

            try
            {
                // Broadcast that we're checking this watch
                await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
                {
                    WatchId = watch.Id,
                    WatchName = watch.Name ?? watch.Url,
                    Status = "Checking",
                    LastError = (string?)null,
                    LastCheck = watch.LastChecked
                }, ct);

                var changeEvent = await watchService.CheckForChangesAsync(watch.Id, ct);
                
                // Get updated watch status
                var updatedWatch = await watchService.GetByIdAsync(watch.Id, ct);
                
                // Broadcast status update after check
                await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
                {
                    WatchId = watch.Id,
                    WatchName = updatedWatch?.Name ?? watch.Url,
                    Status = updatedWatch?.Status.ToString() ?? "Idle",
                    LastError = updatedWatch?.LastError,
                    LastCheck = updatedWatch?.LastChecked
                }, ct);

                if (changeEvent != null)
                {
                    // Notify via SignalR - change detected
                    await hubContext.Clients.Group(dashboardGroup).SendAsync("ChangeDetected", new
                    {
                        WatchId = watch.Id,
                        WatchName = watch.Name ?? watch.Url,
                        ChangeId = changeEvent.Id,
                        Summary = changeEvent.DiffSummary,
                        DetectedAt = changeEvent.DetectedAt,
                        Importance = changeEvent.Importance.ToString(),
                        LinesAdded = changeEvent.LinesAdded,
                        LinesRemoved = changeEvent.LinesRemoved
                    }, ct);

                    // Send notifications if configured
                    if (updatedWatch != null && ShouldNotify(updatedWatch.Notifications, changeEvent))
                    {
                        try
                        {
                            await notificationService.SendNotificationAsync(updatedWatch, changeEvent, ct: ct);
                            
                            // Update change event as notified and persist the change
                            changeEvent.IsNotified = true;
                            changeEvent.NotifiedAt = DateTime.UtcNow;
                            await eventRepo.UpdateAsync(changeEvent, ct);
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
                
                // Broadcast error status
                await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
                {
                    WatchId = watch.Id,
                    WatchName = watch.Name ?? watch.Url,
                    Status = "Error",
                    LastError = ex.Message,
                    LastCheck = DateTime.UtcNow
                }, ct);
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
