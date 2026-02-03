using System.Collections.Concurrent;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Hybrid in-memory + persistent conversation session manager.
/// Sessions are cached in memory for performance and persisted to LiteDB for durability.
/// Automatically recovers sessions from persistent storage on app restart.
/// </summary>
public class ConversationSessionManager : IConversationSessionManager, IDisposable
{
    private readonly ConcurrentDictionary<Guid, ConversationSession> _sessions = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationSessionManager> _logger;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);

    public ConversationSessionManager(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationSessionManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, _cleanupInterval, _cleanupInterval);
        
        // Load persisted sessions on startup (fire and forget, but log errors)
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadPersistedSessionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load persisted sessions on startup");
            }
        });
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

            var session = persistence.LoadSessionAsync(sessionId).GetAwaiter().GetResult();
            if (session != null && !IsExpired(session))
            {
                return session;
            }
            
            // Clean up expired persisted session
            if (session != null)
            {
                persistence.DeleteSessionAsync(sessionId).GetAwaiter().GetResult();
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
        
        // Persist asynchronously (fire and forget, but log errors)
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var persistence = scope.ServiceProvider.GetService<ISessionPersistenceService>();
                if (persistence != null)
                {
                    await persistence.SaveSessionAsync(session, ownerId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist session {SessionId}", session.SessionId);
            }
        });
        
        _logger.LogDebug("Updated session {SessionId}", session.SessionId);
    }

    /// <inheritdoc />
    public void RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
        {
            // Also remove from persistence
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var persistence = scope.ServiceProvider.GetService<ISessionPersistenceService>();
                    if (persistence != null)
                    {
                        await persistence.DeleteSessionAsync(sessionId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete session {SessionId} from persistence", sessionId);
                }
            });
            
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
            
            // Also clean up from persistent storage
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var persistence = scope.ServiceProvider.GetService<ISessionPersistenceService>();
                    if (persistence != null)
                    {
                        await persistence.DeleteExpiredSessionsAsync(_sessionTimeout);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up expired sessions from persistence");
                }
            });
            
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

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _sessions.Clear();
        GC.SuppressFinalize(this);
    }
}
