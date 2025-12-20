using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// User context for background services that need to access data across all users.
/// This context has elevated privileges similar to admin but is only for background processing.
/// </summary>
public class BackgroundServiceUserContext : IUserContext
{
    /// <summary>
    /// The well-known user ID for background services.
    /// Uses Guid.Empty to indicate system-level access.
    /// </summary>
    public static readonly Guid BackgroundServiceOwnerId = Guid.Empty;
    
    private static readonly User BackgroundUser = new()
    {
        Id = BackgroundServiceOwnerId,
        Username = "system",
        DisplayName = "Background Service",
        Email = null,
        Groups = ["admin", "system"],
        IsActive = true
    };
    
    /// <inheritdoc />
    public Guid CurrentUserId => BackgroundServiceOwnerId;
    
    /// <inheritdoc />
    /// <remarks>
    /// Always returns true for background services to allow access to all data.
    /// </remarks>
    public bool IsAuthenticated => true;
    
    /// <inheritdoc />
    /// <remarks>
    /// Background services have admin-level access to see all watches.
    /// </remarks>
    public bool IsAdmin => true;
    
    /// <inheritdoc />
    public User? GetCurrentUser() => BackgroundUser;
    
    /// <inheritdoc />
    public User GetRequiredCurrentUser() => BackgroundUser;
}
