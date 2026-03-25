using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB implementation of session persistence.
/// All operations are serialized through <see cref="ThreadSafeLiteDbContext"/>.
/// </summary>
public class SessionPersistenceService : ISessionPersistenceService
{
    private readonly ThreadSafeLiteDbContext _safeContext;
    private readonly ILogger<SessionPersistenceService> _logger;

    public SessionPersistenceService(ThreadSafeLiteDbContext safeContext, ILogger<SessionPersistenceService> logger)
    {
        _safeContext = safeContext;
        _logger = logger;
    }

    private static ILiteCollection<PersistedSession> Col(ILiteDatabase db)
        => db.GetCollection<PersistedSession>("persisted_sessions");

    public async Task SaveSessionAsync(ConversationSession session, Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var persisted = PersistedSession.FromSession(session, ownerId);
            var existing = col.FindOne(x => x.SessionId == session.SessionId);
            if (existing != null)
                persisted.Id = existing.Id;
            col.Upsert(persisted);
        }, ct);
    }

    public async Task<ConversationSession?> LoadSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var persisted = Col(db).FindOne(x => x.SessionId == sessionId);
            if (persisted == null) return (ConversationSession?)null;
            try { return persisted.ToSession(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize session {Id}", sessionId);
                return null;
            }
        }, ct);
    }

    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db => { Col(db).DeleteMany(x => x.SessionId == sessionId); }, ct);
    }

    public async Task<IReadOnlyList<PersistedSession>> GetActiveSessionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            (IReadOnlyList<PersistedSession>)Col(db).Query()
                .OrderByDescending(x => x.LastActivityAt)
                .ToList(), ct);
    }

    public async Task<IReadOnlyList<PersistedSession>> GetActiveSessionsForOwnerAsync(Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            (IReadOnlyList<PersistedSession>)Col(db).Query()
                .Where(x => x.OwnerId == ownerId)
                .OrderByDescending(x => x.LastActivityAt)
                .ToList(), ct);
    }

    public async Task<int> DeleteExpiredSessionsAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        return await _safeContext.ExecuteAsync(db =>
            Col(db).DeleteMany(x => x.LastActivityAt < cutoff), ct);
    }

    public async Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            Col(db).Exists(x => x.SessionId == sessionId), ct);
    }

    public async Task SaveStateHistoryAsync(Guid sessionId, string stateHistoryJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var persisted = col.FindOne(x => x.SessionId == sessionId);
            if (persisted != null)
            {
                persisted.StateHistoryJson = stateHistoryJson;
                persisted.LastActivityAt = DateTimeOffset.UtcNow;
                col.Update(persisted);
            }
        }, ct);
    }

    public async Task<string> LoadStateHistoryAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            Col(db).FindOne(x => x.SessionId == sessionId)?.StateHistoryJson ?? "[]", ct);
    }
}
