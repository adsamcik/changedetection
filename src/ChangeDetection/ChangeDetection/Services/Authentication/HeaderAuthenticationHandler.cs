using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// Authentication handler that extracts user identity from reverse proxy headers (Authelia, Authentik, etc.).
/// The reverse proxy must be configured to set headers after successful authentication.
/// 
/// SECURITY: This handler includes validation to prevent header injection attacks:
/// - Rejects headers containing control characters or null bytes
/// - Validates username format (alphanumeric with limited special chars)
/// - Validates email format
/// - Enforces maximum length limits
/// </summary>
public partial class HeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthenticationSettings> authSettings,
    IServiceProvider serviceProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// The authentication scheme name for header-based authentication.
    /// </summary>
    public const string SchemeName = "HeaderAuth";
    
    private readonly AuthenticationSettings _authSettings = authSettings.Value;
    
    // Username: alphanumeric, underscores, hyphens, dots, @ (for email-style usernames)
    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.@]+$", RegexOptions.Compiled)]
    private static partial Regex UsernameValidationRegex();
    
    // Basic email validation - more permissive than RFC 5322 but catches obvious issues
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled)]
    private static partial Regex EmailValidationRegex();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Get username from header - this is required
        var usernameRaw = Request.Headers[_authSettings.UsernameHeader].FirstOrDefault();
        
        if (string.IsNullOrWhiteSpace(usernameRaw))
        {
            return AuthenticateResult.NoResult();
        }
        
        // Sanitize and validate username
        var username = SanitizeHeaderValue(usernameRaw);
        if (username is null)
        {
            Logger.LogWarning("Rejected username header containing invalid characters");
            return AuthenticateResult.Fail("Invalid username format: contains prohibited characters.");
        }
        
        if (username.Length > _authSettings.MaxUsernameLength)
        {
            Logger.LogWarning("Rejected username header exceeding maximum length of {MaxLength}", _authSettings.MaxUsernameLength);
            return AuthenticateResult.Fail("Invalid username: exceeds maximum length.");
        }
        
        if (!UsernameValidationRegex().IsMatch(username))
        {
            Logger.LogWarning("Rejected username header with invalid format: {Username}", username);
            return AuthenticateResult.Fail("Invalid username format: must be alphanumeric with allowed special characters (._-@).");
        }
        
        // Get and validate optional headers
        var emailRaw = Request.Headers[_authSettings.EmailHeader].FirstOrDefault();
        var displayNameRaw = Request.Headers[_authSettings.DisplayNameHeader].FirstOrDefault();
        var groupsHeaderRaw = Request.Headers[_authSettings.GroupsHeader].FirstOrDefault();
        
        // Validate email if present
        string? email = null;
        if (!string.IsNullOrWhiteSpace(emailRaw))
        {
            email = SanitizeHeaderValue(emailRaw);
            if (email is null)
            {
                Logger.LogWarning("Rejected email header containing invalid characters");
                return AuthenticateResult.Fail("Invalid email format: contains prohibited characters.");
            }
            
            if (email.Length > _authSettings.MaxEmailLength)
            {
                Logger.LogWarning("Rejected email header exceeding maximum length of {MaxLength}", _authSettings.MaxEmailLength);
                return AuthenticateResult.Fail("Invalid email: exceeds maximum length.");
            }
            
            if (!EmailValidationRegex().IsMatch(email))
            {
                Logger.LogWarning("Rejected email header with invalid format");
                return AuthenticateResult.Fail("Invalid email format.");
            }
        }
        
        // Validate display name if present
        string? displayName = null;
        if (!string.IsNullOrWhiteSpace(displayNameRaw))
        {
            displayName = SanitizeHeaderValue(displayNameRaw);
            if (displayName is null)
            {
                Logger.LogWarning("Rejected display name header containing invalid characters");
                return AuthenticateResult.Fail("Invalid display name: contains prohibited characters.");
            }
            
            if (displayName.Length > _authSettings.MaxDisplayNameLength)
            {
                Logger.LogWarning("Rejected display name header exceeding maximum length of {MaxLength}", _authSettings.MaxDisplayNameLength);
                return AuthenticateResult.Fail("Invalid display name: exceeds maximum length.");
            }
        }
        
        // Validate and parse groups
        string? groupsHeader = null;
        if (!string.IsNullOrWhiteSpace(groupsHeaderRaw))
        {
            groupsHeader = SanitizeHeaderValue(groupsHeaderRaw);
            if (groupsHeader is null)
            {
                Logger.LogWarning("Rejected groups header containing invalid characters");
                return AuthenticateResult.Fail("Invalid groups: contains prohibited characters.");
            }
            
            if (groupsHeader.Length > _authSettings.MaxGroupsLength)
            {
                Logger.LogWarning("Rejected groups header exceeding maximum length of {MaxLength}", _authSettings.MaxGroupsLength);
                return AuthenticateResult.Fail("Invalid groups: exceeds maximum length.");
            }
        }
        
        // Parse groups (comma-separated)
        List<string> groups = string.IsNullOrWhiteSpace(groupsHeader)
            ? []
            : groupsHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        
        // Get or create user via the user service
        using var scope = serviceProvider.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        
        var user = await userService.GetOrCreateFromSsoAsync(
            username,
            email,
            displayName,
            groups,
            Context.RequestAborted);
        
        // Check if user is active
        if (!user.IsActive)
        {
            return AuthenticateResult.Fail("User account is deactivated.");
        }
        
        // Build claims
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(HeaderAuthenticationClaims.UserId, user.Id.ToString())
        };
        
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }
        
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            claims.Add(new Claim(HeaderAuthenticationClaims.DisplayName, user.DisplayName));
        }
        
        // Add group claims
        foreach (var group in user.Groups)
        {
            claims.Add(new Claim(ClaimTypes.Role, group));
            claims.Add(new Claim(HeaderAuthenticationClaims.Group, group));
        }
        
        // Check if user is admin
        var isAdmin = user.Groups.Contains(_authSettings.AdminGroup, StringComparer.OrdinalIgnoreCase);
        if (isAdmin)
        {
            claims.Add(new Claim(HeaderAuthenticationClaims.IsAdmin, "true"));
        }
        
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Sanitizes a header value by checking for and rejecting control characters and null bytes.
    /// Returns null if the value contains prohibited characters, otherwise returns the trimmed value.
    /// </summary>
    /// <param name="value">The raw header value to sanitize.</param>
    /// <returns>The sanitized value, or null if the value contains prohibited characters.</returns>
    private static string? SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        
        // Check for null bytes - common injection attack vector
        if (value.Contains('\0'))
        {
            return null;
        }
        
        // Check for control characters (ASCII 0-31 except tab, and DEL 127)
        // Tab (9), LF (10), CR (13) are sometimes legitimately in headers but we reject them
        // to be conservative for authentication headers
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return null;
            }
        }
        
        return value.Trim();
    }
}

/// <summary>
/// Custom claim types for header-based authentication.
/// </summary>
public static class HeaderAuthenticationClaims
{
    public const string UserId = "cd:userid";
    public const string DisplayName = "cd:displayname";
    public const string Group = "cd:group";
    public const string IsAdmin = "cd:isadmin";
}
