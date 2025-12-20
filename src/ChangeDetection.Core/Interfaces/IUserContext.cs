using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Provides access to the current user context.
/// Implementation varies based on authentication mode:
/// - SingleUser mode: Returns a fixed default user with admin privileges
/// - SSO mode: Returns the authenticated user from Authelia headers
/// </summary>
public interface IUserContext
{
    /// <summary>
    /// Gets the current user's ID.
    /// In single-user mode, this is Guid.Empty.
    /// </summary>
    Guid CurrentUserId { get; }
    
    /// <summary>
    /// Gets whether a user is currently authenticated.
    /// Always true in single-user mode.
    /// </summary>
    bool IsAuthenticated { get; }
    
    /// <summary>
    /// Gets whether the current user has admin privileges.
    /// Always true in single-user mode.
    /// In SSO mode, checks for admin group membership.
    /// </summary>
    bool IsAdmin { get; }
    
    /// <summary>
    /// Gets the current user entity.
    /// Returns null only if not authenticated (SSO mode with missing headers).
    /// </summary>
    User? GetCurrentUser();
    
    /// <summary>
    /// Gets the current user entity, throwing if not authenticated.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no user is authenticated.</exception>
    User GetRequiredCurrentUser();
}
