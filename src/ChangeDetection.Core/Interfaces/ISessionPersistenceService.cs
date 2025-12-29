using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for persisting conversation sessions to durable storage.
/// Enables session recovery after app restart.
/// </summary>
public interface ISessionPersistenceService
{
    /// <summary>
    /// Saves or updates a session in persistent storage.
    /// </summary>
    Task SaveSessionAsync(ConversationSession session, Guid ownerId, CancellationToken ct = default);

    /// <summary>
    /// Loads a session from persistent storage.
    /// Returns null if not found.
    /// </summary>
    Task<ConversationSession?> LoadSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a session from persistent storage.
    /// </summary>
    Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Gets all active sessions (not expired) from persistent storage.
    /// </summary>
    Task<IReadOnlyList<PersistedSession>> GetActiveSessionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all active sessions for a specific owner.
    /// </summary>
    Task<IReadOnlyList<PersistedSession>> GetActiveSessionsForOwnerAsync(Guid ownerId, CancellationToken ct = default);

    /// <summary>
    /// Deletes expired sessions from persistent storage.
    /// </summary>
    Task<int> DeleteExpiredSessionsAsync(TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>
    /// Checks if a session exists in persistent storage.
    /// </summary>
    Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Saves the flow state history for a session.
    /// This is stored separately from the main session for frequent updates during processing.
    /// </summary>
    Task SaveStateHistoryAsync(Guid sessionId, string stateHistoryJson, CancellationToken ct = default);

    /// <summary>
    /// Loads the flow state history for a session.
    /// Returns an empty JSON array "[]" if not found.
    /// </summary>
    Task<string> LoadStateHistoryAsync(Guid sessionId, CancellationToken ct = default);
}
