using System.Security.Claims;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// User context for SSO mode that extracts user information from the HTTP context claims.
/// </summary>
public class SsoUserContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<AuthenticationSettings> authSettings) : IUserContext
{
    private readonly AuthenticationSettings _authSettings = authSettings.Value;
    
    /// <inheritdoc />
    public Guid CurrentUserId
    {
        get
        {
            var userIdClaim = httpContextAccessor.HttpContext?.User.FindFirst(HeaderAuthenticationClaims.UserId);
            return userIdClaim is not null && Guid.TryParse(userIdClaim.Value, out var userId)
                ? userId
                : Guid.Empty;
        }
    }
    
    /// <inheritdoc />
    public bool IsAuthenticated => 
        httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;
    
    /// <inheritdoc />
    public bool IsAdmin =>
        httpContextAccessor.HttpContext?.User.HasClaim(HeaderAuthenticationClaims.IsAdmin, "true") == true;
    
    /// <inheritdoc />
    public User? GetCurrentUser()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }
        
        var claims = httpContext.User;
        var userIdClaim = claims.FindFirst(HeaderAuthenticationClaims.UserId);
        
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return null;
        }
        
        // Reconstruct user from claims (cached from authentication)
        var groups = claims.FindAll(HeaderAuthenticationClaims.Group)
            .Select(c => c.Value)
            .ToList();
        
        return new User
        {
            Id = userId,
            Username = claims.FindFirst(ClaimTypes.Name)?.Value ?? "unknown",
            Email = claims.FindFirst(ClaimTypes.Email)?.Value,
            DisplayName = claims.FindFirst(HeaderAuthenticationClaims.DisplayName)?.Value,
            Groups = groups
        };
    }
    
    /// <inheritdoc />
    public User GetRequiredCurrentUser()
    {
        return GetCurrentUser() 
            ?? throw new InvalidOperationException("No authenticated user in the current context.");
    }
}
