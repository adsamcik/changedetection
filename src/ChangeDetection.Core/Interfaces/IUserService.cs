using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for managing users in SSO mode.
/// Handles auto-provisioning on first login and user lookup.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Gets a user by their unique ID.
    /// </summary>
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    
    /// <summary>
    /// Gets a user by their username (case-insensitive).
    /// </summary>
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    
    /// <summary>
    /// Gets or creates a user from SSO headers.
    /// If the user exists, updates their LastSeen timestamp and any changed profile data.
    /// If the user doesn't exist, creates a new user with the provided data.
    /// </summary>
    /// <param name="username">Username from Remote-User header.</param>
    /// <param name="email">Email from Remote-Email header.</param>
    /// <param name="displayName">Display name from Remote-Name header.</param>
    /// <param name="groups">Groups from Remote-Groups header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The existing or newly created user.</returns>
    Task<User> GetOrCreateFromSsoAsync(
        string username,
        string? email,
        string? displayName,
        IReadOnlyList<string> groups,
        CancellationToken ct = default);
    
    /// <summary>
    /// Gets all users in the system.
    /// </summary>
    Task<IEnumerable<User>> GetAllAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Updates an existing user.
    /// </summary>
    Task UpdateAsync(User user, CancellationToken ct = default);
    
    /// <summary>
    /// Deactivates a user, preventing them from accessing the system.
    /// </summary>
    Task DeactivateAsync(Guid userId, CancellationToken ct = default);
    
    /// <summary>
    /// Reactivates a previously deactivated user.
    /// </summary>
    Task ReactivateAsync(Guid userId, CancellationToken ct = default);
}
