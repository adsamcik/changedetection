using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Hubs;

/// <summary>
/// SignalR hub for real-time change notifications.
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
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
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
