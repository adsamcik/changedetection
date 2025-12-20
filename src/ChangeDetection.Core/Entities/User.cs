namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents an authenticated user in SSO mode.
/// Users are auto-provisioned on first login from Authelia headers.
/// </summary>
public class User
{
    /// <summary>
    /// Unique identifier for the user.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Username from Authelia (Remote-User header).
    /// This is the primary identifier for matching users across sessions.
    /// </summary>
    public required string Username { get; set; }
    
    /// <summary>
    /// Email address from Authelia (Remote-Email header).
    /// </summary>
    public string? Email { get; set; }
    
    /// <summary>
    /// Display name from Authelia (Remote-Name header).
    /// </summary>
    public string? DisplayName { get; set; }
    
    /// <summary>
    /// Group memberships from Authelia (Remote-Groups header).
    /// Used for authorization (e.g., admin group membership).
    /// </summary>
    public List<string> Groups { get; set; } = [];
    
    /// <summary>
    /// When the user first authenticated.
    /// </summary>
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the user last authenticated.
    /// Updated on each request.
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Whether the user account is active.
    /// Inactive users cannot access the system even with valid SSO headers.
    /// </summary>
    public bool IsActive { get; set; } = true;
    
    /// <summary>
    /// Gets the display name to show in the UI.
    /// Falls back to username if display name is not set.
    /// </summary>
    public string GetDisplayName() => DisplayName ?? Username;
}
