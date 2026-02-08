using System.Collections.Concurrent;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Hubs;
using ChangeDetection.Services.Authentication;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that periodically checks watches for changes.
/// Processes all watches across all users using BackgroundServiceUserContext.
/// Concurrency is controlled via MaxConcurrentChecks setting.
/// </summary>
public class ChangeCheckBackgroundService : BackgroundService
{
    private const int DefaultMaxConcurrentChecks = 5;
    private static readonly ConcurrentDictionary<Guid, byte> _runningWatches = new();
    
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
        var settingsRepo = scope.ServiceProvider.GetRequiredService<IRepository<AppSettings>>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChangeDetectionHub>>();

        var pendingWatches = await watchService.GetWatchesDueForCheckAsync(ct);
        var watchList = pendingWatches.ToList();

        if (watchList.Count == 0)
        {
            return;
        }

        // Load concurrency settings
        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault();
        var maxConcurrent = settings?.MaxConcurrentChecks ?? DefaultMaxConcurrentChecks;
        
        // Clamp to reasonable bounds
        maxConcurrent = Math.Max(1, Math.Min(maxConcurrent, 50));

        _logger.LogInformation(
            "Checking {Count} watches for changes (max concurrent: {MaxConcurrent})", 
            watchList.Count, 
            maxConcurrent);

        // Use semaphore to control concurrency
        using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        
        var tasks = watchList.Select(async watch =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await CheckSingleWatchAsync(scope.ServiceProvider, watch, hubContext, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        // Wait for all checks to complete (or cancel)
        await Task.WhenAll(tasks);
    }

    private async Task CheckSingleWatchAsync(
        IServiceProvider serviceProvider,
        WatchedSite watch,
        IHubContext<ChangeDetectionHub> hubContext,
        CancellationToken ct)
    {
        if (!_runningWatches.TryAdd(watch.Id, 0))
        {
            _logger.LogDebug("Skipping watch {WatchId} — already being checked", watch.Id);
            return;
        }
        try
        {
            // Each watch check gets its own scoped services to avoid thread safety issues
            using var watchScope = serviceProvider.CreateScope();
            var watchService = watchScope.ServiceProvider.GetRequiredService<IWatchService>();
            var notificationService = watchScope.ServiceProvider.GetRequiredService<INotificationService>();
            var eventRepo = watchScope.ServiceProvider.GetRequiredService<IRepository<ChangeEvent>>();
        
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

                // Use composable pipeline executor if pipeline definition exists
                if (!string.IsNullOrEmpty(watch.PipelineDefinitionJson))
                {
                    await CheckWithPipelineExecutorAsync(watchScope.ServiceProvider, watch, hubContext, dashboardGroup, ct);
                }
                else
                {
                    await CheckWithLegacyServiceAsync(watchService, notificationService, eventRepo,
                        watch, hubContext, dashboardGroup, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown, don't log as error
                _logger.LogDebug("Watch check cancelled for {WatchId}", watch.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking watch {WatchId} ({Url})", watch.Id, watch.Url);
            
                // Broadcast error status
                try
                {
                    await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
                    {
                        WatchId = watch.Id,
                        WatchName = watch.Name ?? watch.Url,
                        Status = "Error",
                        LastError = ex.Message,
                        LastCheck = DateTime.UtcNow
                    }, CancellationToken.None); // Use None to ensure error status is broadcast even on cancellation
                }
                catch (Exception broadcastEx)
                {
                    _logger.LogWarning(broadcastEx, "Failed to broadcast error status for watch {WatchId}", watch.Id);
                }
            }
        }
        finally
        {
            _runningWatches.TryRemove(watch.Id, out _);
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

    /// <summary>
    /// Runs the legacy hardcoded check flow for watches without a composable pipeline definition.
    /// </summary>
    private async Task CheckWithLegacyServiceAsync(
        IWatchService watchService,
        INotificationService notificationService,
        IRepository<ChangeEvent> eventRepo,
        WatchedSite watch,
        IHubContext<ChangeDetectionHub> hubContext,
        string dashboardGroup,
        CancellationToken ct)
    {
        var changeEvent = await watchService.CheckForChangesAsync(watch.Id, ct);

        var updatedWatch = await watchService.GetByIdAsync(watch.Id, ct);

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

            if (updatedWatch != null && ShouldNotify(updatedWatch.Notifications, changeEvent))
            {
                try
                {
                    await notificationService.SendNotificationAsync(updatedWatch, changeEvent, ct: ct);
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

    /// <summary>
    /// Runs the composable pipeline executor for watches with a PipelineDefinitionJson.
    /// </summary>
    private async Task CheckWithPipelineExecutorAsync(
        IServiceProvider sp,
        WatchedSite watch,
        IHubContext<ChangeDetectionHub> hubContext,
        string dashboardGroup,
        CancellationToken ct)
    {
        var pipelineExecutor = sp.GetRequiredService<IPipelineExecutor>();
        var stateStore = sp.GetRequiredService<IBlockStateStore>();
        var watchRepo = sp.GetRequiredService<IRepository<WatchedSite>>();

        var definition = PipelineSerializer.Deserialize(watch.PipelineDefinitionJson!);
        if (definition is null)
        {
            _logger.LogWarning("Watch {WatchId} has invalid PipelineDefinitionJson, skipping", watch.Id);
            await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
            {
                WatchId = watch.Id,
                WatchName = watch.Name ?? watch.Url,
                Status = "Error",
                LastError = "Invalid pipeline definition",
                LastCheck = DateTime.UtcNow
            }, ct);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        _logger.LogDebug("Executing composable pipeline for watch {WatchId}", watch.Id);
        var result = await pipelineExecutor.ExecuteAsync(definition, watch.Id, stateStore, page: null, timeoutCts.Token);

        // Update watch status
        watch.LastChecked = DateTime.UtcNow;
        watch.LastError = result.Success ? null : result.Error;
        await watchRepo.UpdateAsync(watch, ct);

        await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
        {
            WatchId = watch.Id,
            WatchName = watch.Name ?? watch.Url,
            Status = result.Success ? "Idle" : "Error",
            LastError = result.Error,
            LastCheck = watch.LastChecked
        }, ct);

        if (result.IsDegraded)
        {
            _logger.LogWarning(
                "Pipeline execution for watch {WatchId} completed in DEGRADED state — some blocks may have failed non-critically",
                watch.Id);
        }

        _logger.LogInformation(
            "Pipeline execution for watch {WatchId} completed: Success={Success}, Blocks={BlockCount}, Degraded={Degraded}",
            watch.Id, result.Success, result.BlockResults.Count, result.IsDegraded);
    }
}
