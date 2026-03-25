using System.Collections.Concurrent;
using System.Threading.Channels;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Hybrid in-memory + persistent conversation session manager.
/// Sessions are cached in memory for performance and persisted to LiteDB for durability.
/// Automatically recovers sessions from persistent storage on app restart.
/// Uses a dedicated persistence queue instead of fire-and-forget Task.Run.
/// Implements IHostedService so session recovery is awaited during startup.
/// </summary>
public class ConversationSessionManager : IConversationSessionManager, IHostedService, IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<Guid, ConversationSession> _sessions = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationSessionManager> _logger;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);
    
    // Persistence queue: replaces fire-and-forget Task.Run
    // Uses Wait mode with 1024 capacity to apply back-pressure instead of silently dropping commands
    private readonly Channel<PersistenceCommand> _persistenceChannel = Channel.CreateBounded<PersistenceCommand>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _persistenceLoop;
    private readonly CancellationTokenSource _shutdownCts = new();

    public ConversationSessionManager(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationSessionManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Timer starts disabled; enabled in StartAsync after sessions are loaded
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        
        // Start background persistence loop
        _persistenceLoop = Task.Run(() => ProcessPersistenceQueueAsync(_shutdownCts.Token));
    }

    /// <summary>
    /// Called by the host on startup. Loads persisted sessions before the app accepts requests.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadPersistedSessionsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session recovery failed but service will continue with empty session cache");
        }
        
        // Start cleanup timer after sessions are loaded
        _cleanupTimer.Change(_cleanupInterval, _cleanupInterval);
    }

    /// <summary>
    /// Called by the host on shutdown. Flushes pending persistence commands.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken);
    }

    private async Task ProcessPersistenceQueueAsync(CancellationToken ct)
    {
        await foreach (var command in _persistenceChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var persistence = scope.ServiceProvider.GetService<ISessionPersistenceService>();
                if (persistence == null) continue;

                switch (command)
                {
                    case PersistenceCommand.Save save:
                        await persistence.SaveSessionAsync(save.Session, save.OwnerId, ct);
                        break;
                    case PersistenceCommand.Delete delete:
                        await persistence.DeleteSessionAsync(delete.SessionId, ct);
                        break;
                    case PersistenceCommand.DeleteExpired deleteExpired:
                        await persistence.DeleteExpiredSessionsAsync(deleteExpired.MaxAge, ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to process persistence command {CommandType}", command.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Flushes all pending persistence commands. Call during graceful shutdown.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Complete the channel so no new items are accepted
        _persistenceChannel.Writer.TryComplete();
        
        // Wait for the loop to drain remaining items
        try
        {
            await _persistenceLoop.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Persistence flush timed out after 10 seconds");
        }
    }

    private async Task LoadPersistedSessionsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var persistence = scope.ServiceProvider.GetService<ISessionPersistenceService>();
        if (persistence == null)
        {
            _logger.LogDebug("Session persistence service not available, skipping session recovery");
            return;
        }

        var persistedSessions = await persistence.GetActiveSessionsAsync();
        var loadedCount = 0;

        foreach (var persisted in persistedSessions)
        {
            // Skip if already in memory (shouldn't happen on startup, but be safe)
            if (_sessions.ContainsKey(persisted.SessionId))
                continue;

            // Skip expired sessions
            if (DateTimeOffset.UtcNow - persisted.LastActivityAt > _sessionTimeout)
            {
                await persistence.DeleteSessionAsync(persisted.SessionId);
                continue;
            }

            try
            {
                var session = persisted.ToSession();
                _sessions[session.SessionId] = session;
                loadedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore session {SessionId}", persisted.SessionId);
                await persistence.DeleteSessionAsync(persisted.SessionId);
            }
        }

        if (loadedCount > 0)
        {
            _logger.LogInformation("Restored {Count} conversation sessions from persistent storage", loadedCount);
        }
    }

    /// <inheritdoc />
    public event Action<Guid>? SessionExpired;

    /// <inheritdoc />
    public int ActiveSessionCount => _sessions.Count;

    /// <inheritdoc />
    public ConversationSession CreateSession()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };
        _sessions[session.SessionId] = session;
        _logger.LogDebug("Created session {SessionId}", session.SessionId);
        return session;
    }

    /// <inheritdoc />
    public ConversationSession GetOrCreateSession(Guid sessionId)
    {
        // Try to get existing session first
        if (_sessions.TryGetValue(sessionId, out var existingSession))
        {
            if (!IsExpired(existingSession))
            {
                existingSession.Touch();
                _logger.LogDebug("Retrieved existing session {SessionId}", sessionId);
                return existingSession;
            }
            
            // Remove expired session
            _sessions.TryRemove(sessionId, out _);
            _logger.LogDebug("Removed expired session {SessionId}", sessionId);
        }

        // Try to load from persistent storage
        var persistedSession = TryLoadFromPersistence(sessionId);
        if (persistedSession != null)
        {
            _sessions[sessionId] = persistedSession;
            _logger.LogDebug("Restored session {SessionId} from persistent storage", sessionId);
            return persistedSession;
        }

        // Create new session with the specified ID
        var session = new ConversationSession { SessionId = sessionId };
        _sessions[sessionId] = session;
        _logger.LogDebug("Created new session with specified ID {SessionId}", sessionId);
        return session;
    }

    private ConversationSession? TryLoadFromPersistence(Guid sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var persistence = scope.ServiceProvider.GetService<ISessionPersistenceService>();
            if (persistence == null)
                return null;

            // Use Task.Run to avoid sync-over-async deadlock on the DB semaphore
            var session = Task.Run(() => persistence.LoadSessionAsync(sessionId)).GetAwaiter().GetResult();
            if (session != null && !IsExpired(session))
            {
                return session;
            }
            
            if (session != null)
            {
                Task.Run(() => persistence.DeleteSessionAsync(sessionId)).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session {SessionId} from persistence", sessionId);
        }

        return null;
    }

    /// <inheritdoc />
    public ConversationSession? GetSession(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            if (IsExpired(session))
            {
                _logger.LogDebug("Session {SessionId} expired", sessionId);
                _sessions.TryRemove(sessionId, out _);
                return null;
            }

            session.Touch();
            return session;
        }

        return null;
    }

    /// <inheritdoc />
    public void UpdateSession(ConversationSession session)
    {
        UpdateSession(session, Guid.Empty);
    }

    /// <summary>
    /// Updates a session and persists it to storage.
    /// </summary>
    public void UpdateSession(ConversationSession session, Guid ownerId)
    {
        session.Touch();
        _sessions[session.SessionId] = session;
        
        // Queue persistence (replaces fire-and-forget Task.Run)
        if (!_persistenceChannel.Writer.TryWrite(new PersistenceCommand.Save(session, ownerId)))
            _logger.LogWarning("Failed to enqueue save for session {SessionId} — channel completed", session.SessionId);
        
        _logger.LogDebug("Updated session {SessionId}", session.SessionId);
    }

    /// <inheritdoc />
    public void RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
        {
            // Queue deletion (replaces fire-and-forget Task.Run)
            if (!_persistenceChannel.Writer.TryWrite(new PersistenceCommand.Delete(sessionId)))
                _logger.LogWarning("Failed to enqueue delete for session {SessionId} — channel completed", sessionId);
            
            _logger.LogDebug("Removed session {SessionId}", sessionId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ConversationSession> GetSessionsAwaitingInput()
    {
        return _sessions.Values
            .Where(s => !IsExpired(s) && s.AwaitingUserInput)
            .OrderByDescending(s => s.LastActivityAt)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<ConversationSession> GetAllActiveSessions()
    {
        return _sessions.Values
            .Where(s => !IsExpired(s) && !s.IsCompleted && (s.AwaitingUserInput || !string.IsNullOrEmpty(s.PendingInput) || !string.IsNullOrEmpty(s.DisplayName)))
            .OrderByDescending(s => s.LastActivityAt)
            .ToList();
    }

    private bool IsExpired(ConversationSession session)
    {
        return DateTimeOffset.UtcNow - session.LastActivityAt > _sessionTimeout;
    }

    private void CleanupExpiredSessions(object? state)
    {
        var expiredCount = 0;
        var expiredSessionIds = new List<Guid>();
        
        foreach (var kvp in _sessions)
        {
            if (IsExpired(kvp.Value))
            {
                if (_sessions.TryRemove(kvp.Key, out _))
                {
                    expiredCount++;
                    expiredSessionIds.Add(kvp.Key);
                }
            }
        }

        if (expiredCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired sessions, {Remaining} active", 
                expiredCount, _sessions.Count);
            
            // Queue persistence cleanup (replaces fire-and-forget Task.Run)
            if (!_persistenceChannel.Writer.TryWrite(new PersistenceCommand.DeleteExpired(_sessionTimeout)))
                _logger.LogWarning("Failed to enqueue expired session cleanup — channel completed");
            
            // Notify subscribers about expired sessions so they can clean up related resources
            foreach (var sessionId in expiredSessionIds)
            {
                try
                {
                    SessionExpired?.Invoke(sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error notifying SessionExpired subscriber for session {SessionId}", sessionId);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        _persistenceChannel.Writer.TryComplete();
        
        try
        {
            await _persistenceLoop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Persistence loop did not complete within 5s during dispose");
        }
        catch (OperationCanceledException) { }
        
        _cleanupTimer.Dispose();
        _sessions.Clear();
        _shutdownCts.Dispose();
        
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _persistenceChannel.Writer.TryComplete();
        
        // Wait for pending persistence operations to complete
        try
        {
            _persistenceLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException) { }
        catch (TimeoutException) { }
        
        _cleanupTimer.Dispose();
        _sessions.Clear();
        _shutdownCts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Internal command types for the persistence queue.
    /// </summary>
    private abstract record PersistenceCommand
    {
        public record Save(ConversationSession Session, Guid OwnerId) : PersistenceCommand;
        public record Delete(Guid SessionId) : PersistenceCommand;
        public record DeleteExpired(TimeSpan MaxAge) : PersistenceCommand;
    }
}
