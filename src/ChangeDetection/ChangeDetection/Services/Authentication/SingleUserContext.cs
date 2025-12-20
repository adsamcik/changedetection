using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// User context for single-user mode.
/// Always returns a default user with Guid.Empty as ID and admin privileges.
/// </summary>
public class SingleUserContext : IUserContext
{
    /// <summary>
    /// The well-known user ID for single-user mode.
    /// All data created in single-user mode will have this as OwnerId.
    /// Admins in SSO mode can also see records with this OwnerId.
    /// </summary>
    public static readonly Guid SingleUserOwnerId = Guid.Empty;
    
    private static readonly User DefaultUser = new()
    {
        Id = SingleUserOwnerId,
        Username = "default",
        DisplayName = "Default User",
        Email = null,
        Groups = ["admin"],
        IsActive = true
    };
    
    /// <inheritdoc />
    public Guid CurrentUserId => SingleUserOwnerId;
    
    /// <inheritdoc />
    public bool IsAuthenticated => true;
    
    /// <inheritdoc />
    public bool IsAdmin => true;
    
    /// <inheritdoc />
    public User? GetCurrentUser() => DefaultUser;
    
    /// <inheritdoc />
    public User GetRequiredCurrentUser() => DefaultUser;
}
