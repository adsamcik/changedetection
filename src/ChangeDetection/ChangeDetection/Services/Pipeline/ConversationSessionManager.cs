using System.Collections.Concurrent;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// In-memory conversation session manager with sliding expiration.
/// Sessions are never persisted and expire after 30 minutes of inactivity.
/// </summary>
public class ConversationSessionManager : IConversationSessionManager, IDisposable
{
    private readonly ConcurrentDictionary<Guid, ConversationSession> _sessions = new();
    private readonly ILogger<ConversationSessionManager> _logger;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);

    public ConversationSessionManager(ILogger<ConversationSessionManager> logger)
    {
        _logger = logger;
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, _cleanupInterval, _cleanupInterval);
    }

    /// <inheritdoc />
    public int ActiveSessionCount => _sessions.Count;

    /// <inheritdoc />
    public ConversationSession CreateSession()
    {
        var session = new ConversationSession();
        _sessions[session.SessionId] = session;
        _logger.LogDebug("Created session {SessionId}", session.SessionId);
        return session;
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
        session.Touch();
        _sessions[session.SessionId] = session;
        _logger.LogDebug("Updated session {SessionId}", session.SessionId);
    }

    /// <inheritdoc />
    public void RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
        {
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

    private bool IsExpired(ConversationSession session)
    {
        return DateTimeOffset.UtcNow - session.LastActivityAt > _sessionTimeout;
    }

    private void CleanupExpiredSessions(object? state)
    {
        var expiredCount = 0;
        foreach (var kvp in _sessions)
        {
            if (IsExpired(kvp.Value))
            {
                if (_sessions.TryRemove(kvp.Key, out _))
                {
                    expiredCount++;
                }
            }
        }

        if (expiredCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired sessions, {Remaining} active", 
                expiredCount, _sessions.Count);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _sessions.Clear();
        GC.SuppressFinalize(this);
    }
}
