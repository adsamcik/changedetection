using System.Net;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Configuration for authentication mode.
/// </summary>
public class AuthenticationSettings
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Authentication";
    
    /// <summary>
    /// The authentication mode to use.
    /// </summary>
    public AuthenticationMode Mode { get; set; } = AuthenticationMode.SingleUser;
    
    /// <summary>
    /// The group name that grants admin privileges in SSO mode.
    /// Users in this group can manage LLM providers and see orphaned data.
    /// </summary>
    public string AdminGroup { get; set; } = "changedetection-admins";
    
    /// <summary>
    /// Header name for the username from the reverse proxy.
    /// </summary>
    public string UsernameHeader { get; set; } = "Remote-User";
    
    /// <summary>
    /// Header name for the email from the reverse proxy.
    /// </summary>
    public string EmailHeader { get; set; } = "Remote-Email";
    
    /// <summary>
    /// Header name for the display name from the reverse proxy.
    /// </summary>
    public string DisplayNameHeader { get; set; } = "Remote-Name";
    
    /// <summary>
    /// Header name for the groups from the reverse proxy.
    /// Groups are expected to be comma-separated.
    /// </summary>
    public string GroupsHeader { get; set; } = "Remote-Groups";
    
    /// <summary>
    /// List of trusted proxy IP addresses or CIDR ranges.
    /// Forwarded headers will only be accepted from these sources.
    /// Examples: "127.0.0.1", "10.0.0.0/8", "::1", "fd00::/8"
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];
    
    /// <summary>
     /// Maximum length allowed for username header values.
     /// Values exceeding this length will be rejected.
     /// </summary>
    public int MaxUsernameLength { get; set; } = 256;
    
    /// <summary>
    /// Maximum length allowed for email header values.
    /// Values exceeding this length will be rejected.
    /// </summary>
    public int MaxEmailLength { get; set; } = 320;
    
    /// <summary>
    /// Maximum length allowed for display name header values.
    /// Values exceeding this length will be rejected.
    /// </summary>
    public int MaxDisplayNameLength { get; set; } = 256;
    
    /// <summary>
    /// Maximum length allowed for groups header values.
    /// Values exceeding this length will be rejected.
    /// </summary>
    public int MaxGroupsLength { get; set; } = 4096;
}

/// <summary>
/// Available authentication modes.
/// </summary>
public enum AuthenticationMode
{
    /// <summary>
    /// Single-user mode with no authentication.
    /// All data belongs to a default user with Guid.Empty as OwnerId.
    /// </summary>
    SingleUser,
    
    /// <summary>
    /// SSO mode with Authelia (or compatible) reverse proxy authentication.
    /// Users are identified by headers set by the reverse proxy.
    /// </summary>
    SSO
}
