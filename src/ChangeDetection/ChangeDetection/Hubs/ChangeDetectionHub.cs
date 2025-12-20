using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Hubs;

/// <summary>
/// SignalR hub for real-time change notifications and watch status updates.
/// In SSO mode, users are added to user-specific groups for tenant isolation.
/// In single-user mode, all clients share a global dashboard group.
/// </summary>
public class ChangeDetectionHub(
    ILogger<ChangeDetectionHub> logger,
    IUserContext userContext) : Hub
{
    /// <summary>
    /// Gets the dashboard group name for the current user.
    /// </summary>
    private string GetUserDashboardGroup()
    {
        var userId = userContext.CurrentUserId;
        
        // In single-user mode (Guid.Empty) or when admin viewing orphaned data,
        // use a global group. Otherwise use user-specific group.
        return userId == Guid.Empty 
            ? "dashboard" 
            : $"dashboard-{userId}";
    }
    
    public override async Task OnConnectedAsync()
    {
        var userId = userContext.CurrentUserId;
        logger.LogInformation("Client connected: {ConnectionId} for user {UserId}", Context.ConnectionId, userId);
        
        // Add to user-specific dashboard group
        var dashboardGroup = GetUserDashboardGroup();
        await Groups.AddToGroupAsync(Context.ConnectionId, dashboardGroup);
        
        // If admin, also add to the global dashboard to see orphaned watches
        if (userContext.IsAdmin && userId != Guid.Empty)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
        }
        
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = userContext.CurrentUserId;
        logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        
        var dashboardGroup = GetUserDashboardGroup();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, dashboardGroup);
        
        if (userContext.IsAdmin && userId != Guid.Empty)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, "dashboard");
        }
        
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to updates for a specific watch.
    /// </summary>
    public async Task SubscribeToWatch(Guid watchId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"watch-{watchId}");
        logger.LogDebug("Client {ConnectionId} subscribed to watch {WatchId}", Context.ConnectionId, watchId);
    }

    /// <summary>
    /// Unsubscribe from updates for a specific watch.
    /// </summary>
    public async Task UnsubscribeFromWatch(Guid watchId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"watch-{watchId}");
        logger.LogDebug("Client {ConnectionId} unsubscribed from watch {WatchId}", Context.ConnectionId, watchId);
    }
}

/// <summary>
/// Event data for watch status changes.
/// </summary>
public record WatchStatusChangedEvent(
    Guid WatchId,
    string WatchName,
    string Status,
    string? LastError,
    DateTime? LastCheck);

/// <summary>
/// Event data for change detection.
/// </summary>
public record ChangeDetectedEvent(
    Guid WatchId,
    string WatchName,
    Guid ChangeId,
    string? Summary,
    DateTime DetectedAt,
    string Importance,
    int LinesAdded,
    int LinesRemoved);

/// <summary>
/// Event data for watch creation.
/// </summary>
public record WatchCreatedEvent(
    Guid WatchId,
    string Url,
    string Name);

/// <summary>
/// Event data for setup session changes.
/// </summary>
public record SetupSessionUpdatedEvent(
    Guid SessionId,
    string DisplayName,
    string? CurrentPrompt,
    bool IsProcessing,
    bool IsCompleted,
    bool IsCancelled,
    bool IsBackgrounded = false,
    string? CurrentStage = null);
