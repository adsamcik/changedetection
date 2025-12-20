using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Background service that subscribes to session expiration events from ConversationSessionManager
/// and cleans up the corresponding entries in SetupConversationHub's static dictionaries.
/// This prevents memory leaks from sessions that expire naturally without explicit cleanup.
/// Also performs periodic defensive cleanup to catch any orphaned entries.
/// </summary>
public class SetupSessionCleanupService(
    IConversationSessionManager sessionManager,
    ILogger<SetupSessionCleanupService> logger) : IHostedService, IDisposable
{
    private Timer? _defensiveCleanupTimer;
    private static readonly TimeSpan DefensiveCleanupInterval = TimeSpan.FromMinutes(10);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessionManager.SessionExpired += OnSessionExpired;
        
        // Start defensive cleanup timer to catch any orphaned entries
        _defensiveCleanupTimer = new Timer(
            DoDefensiveCleanup, 
            null, 
            DefensiveCleanupInterval, 
            DefensiveCleanupInterval);
        
        logger.LogInformation(
            "SetupSessionCleanupService started, subscribed to session expiration events. " +
            "Defensive cleanup runs every {Interval} minutes",
            DefensiveCleanupInterval.TotalMinutes);
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sessionManager.SessionExpired -= OnSessionExpired;
        _defensiveCleanupTimer?.Change(Timeout.Infinite, 0);
        
        logger.LogInformation("SetupSessionCleanupService stopped");
        return Task.CompletedTask;
    }

    private void OnSessionExpired(Guid sessionId)
    {
        var removed = SetupConversationHub.CleanupExpiredSession(sessionId);
        
        if (removed)
        {
            logger.LogDebug(
                "Cleaned up expired session {SessionId} from hub dictionaries. " +
                "Current counts - PipelineSessions: {PipelineCount}, StateHistories: {HistoryCount}",
                sessionId,
                SetupConversationHub.PipelineSessionCount,
                SetupConversationHub.StateHistoryCount);
        }
    }

    private void DoDefensiveCleanup(object? state)
    {
        try
        {
            var cleanedUp = SetupConversationHub.DefensiveCleanup(sessionManager);
            
            if (cleanedUp > 0)
            {
                logger.LogInformation(
                    "Defensive cleanup removed {Count} orphaned entries. " +
                    "Current counts - PipelineSessions: {PipelineCount}, StateHistories: {HistoryCount}",
                    cleanedUp,
                    SetupConversationHub.PipelineSessionCount,
                    SetupConversationHub.StateHistoryCount);
            }
            else
            {
                logger.LogDebug(
                    "Defensive cleanup completed, no orphaned entries found. " +
                    "Current counts - PipelineSessions: {PipelineCount}, StateHistories: {HistoryCount}",
                    SetupConversationHub.PipelineSessionCount,
                    SetupConversationHub.StateHistoryCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during defensive cleanup");
        }
    }

    public void Dispose()
    {
        _defensiveCleanupTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
