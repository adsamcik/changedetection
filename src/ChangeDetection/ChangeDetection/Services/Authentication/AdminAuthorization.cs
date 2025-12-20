using ChangeDetection.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// Authorization requirement for admin access.
/// </summary>
public class AdminRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Handles admin authorization by checking group membership.
/// In SSO mode, checks if user is in the configured admin group.
/// Always succeeds in single-user mode (handled via SingleUserContext being admin).
/// </summary>
public class AdminRequirementHandler(
    IOptions<AuthenticationSettings> authSettings) : AuthorizationHandler<AdminRequirement>
{
    private readonly AuthenticationSettings _authSettings = authSettings.Value;
    
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminRequirement requirement)
    {
        // Check for admin claim (set by HeaderAuthenticationHandler)
        if (context.User.HasClaim(HeaderAuthenticationClaims.IsAdmin, "true"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
        
        // Check for admin group role claim
        if (context.User.IsInRole(_authSettings.AdminGroup))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
        
        // Check group claims directly
        var groups = context.User.FindAll(HeaderAuthenticationClaims.Group)
            .Select(c => c.Value);
        
        if (groups.Contains(_authSettings.AdminGroup, StringComparer.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
        
        return Task.CompletedTask;
    }
}

/// <summary>
/// Authorization policy names.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Policy requiring admin privileges.
    /// </summary>
    public const string Admin = "Admin";
    
    /// <summary>
    /// Policy requiring any authenticated user.
    /// </summary>
    public const string Authenticated = "Authenticated";
}
