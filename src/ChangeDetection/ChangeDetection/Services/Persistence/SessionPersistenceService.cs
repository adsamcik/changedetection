using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB implementation of session persistence.
/// Stores conversation sessions for recovery after restart.
/// </summary>
public class SessionPersistenceService(LiteDbContext context) : ISessionPersistenceService
{
    private readonly ILiteCollection<PersistedSession> _collection = InitializeCollection(context);

    private static ILiteCollection<PersistedSession> InitializeCollection(LiteDbContext context)
    {
        var collection = context.Database.GetCollection<PersistedSession>("persisted_sessions");
        
        // Indexes for efficient queries
        collection.EnsureIndex(x => x.SessionId, unique: true);
        collection.EnsureIndex(x => x.OwnerId);
        collection.EnsureIndex(x => x.LastActivityAt);
        collection.EnsureIndex(x => x.AwaitingUserInput);
        
        return collection;
    }

    public Task SaveSessionAsync(ConversationSession session, Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var persisted = PersistedSession.FromSession(session, ownerId);
        
        // Upsert - update if exists, insert if not
        var existing = _collection.FindOne(x => x.SessionId == session.SessionId);
        if (existing != null)
        {
            persisted.Id = existing.Id;
            _collection.Update(persisted);
        }
        else
        {
            _collection.Insert(persisted);
        }
        
        return Task.CompletedTask;
    }

    public Task<ConversationSession?> LoadSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var persisted = _collection.FindOne(x => x.SessionId == sessionId);
        if (persisted == null)
            return Task.FromResult<ConversationSession?>(null);
        
        try
        {
            var session = persisted.ToSession();
            return Task.FromResult<ConversationSession?>(session);
        }
        catch (Exception)
        {
            // If deserialization fails, treat as not found
            return Task.FromResult<ConversationSession?>(null);
        }
    }

    public Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collection.DeleteMany(x => x.SessionId == sessionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PersistedSession>> GetActiveSessionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var sessions = _collection.Query()
            .OrderByDescending(x => x.LastActivityAt)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PersistedSession>>(sessions);
    }

    public Task<IReadOnlyList<PersistedSession>> GetActiveSessionsForOwnerAsync(Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var sessions = _collection.Query()
            .Where(x => x.OwnerId == ownerId)
            .OrderByDescending(x => x.LastActivityAt)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PersistedSession>>(sessions);
    }

    public Task<int> DeleteExpiredSessionsAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var deleted = _collection.DeleteMany(x => x.LastActivityAt < cutoff);
        
        return Task.FromResult(deleted);
    }

    public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_collection.Exists(x => x.SessionId == sessionId));
    }

    public Task SaveStateHistoryAsync(Guid sessionId, string stateHistoryJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var persisted = _collection.FindOne(x => x.SessionId == sessionId);
        if (persisted != null)
        {
            persisted.StateHistoryJson = stateHistoryJson;
            persisted.LastActivityAt = DateTimeOffset.UtcNow;
            _collection.Update(persisted);
        }
        
        return Task.CompletedTask;
    }

    public Task<string> LoadStateHistoryAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var persisted = _collection.FindOne(x => x.SessionId == sessionId);
        return Task.FromResult(persisted?.StateHistoryJson ?? "[]");
    }
}
