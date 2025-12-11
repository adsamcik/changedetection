using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Hubs;

/// <summary>
/// SignalR hub for real-time change notifications and watch status updates.
/// </summary>
public class ChangeDetectionHub : Hub
{
    private readonly ILogger<ChangeDetectionHub> _logger;

    public ChangeDetectionHub(ILogger<ChangeDetectionHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        // Add to global dashboard group for broadcasts
        await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "dashboard");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to updates for a specific watch.
    /// </summary>
    public async Task SubscribeToWatch(Guid watchId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"watch-{watchId}");
        _logger.LogDebug("Client {ConnectionId} subscribed to watch {WatchId}", Context.ConnectionId, watchId);
    }

    /// <summary>
    /// Unsubscribe from updates for a specific watch.
    /// </summary>
    public async Task UnsubscribeFromWatch(Guid watchId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"watch-{watchId}");
        _logger.LogDebug("Client {ConnectionId} unsubscribed from watch {WatchId}", Context.ConnectionId, watchId);
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
