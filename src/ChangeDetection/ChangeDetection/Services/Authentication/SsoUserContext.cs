using System.Security.Claims;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// User context for SSO mode that extracts user information from the HTTP context claims.
/// Supports ambient context override for background services.
/// </summary>
public class SsoUserContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<AuthenticationSettings> authSettings) : IUserContext
{
    private readonly AuthenticationSettings _authSettings = authSettings.Value;
    
    /// <summary>
    /// Gets whether an ambient context override is active (e.g., for background services).
    /// </summary>
    private bool HasAmbientOverride => AmbientUserContext.Current != null;
    
    /// <inheritdoc />
    public Guid CurrentUserId
    {
        get
        {
            // Check for ambient context override (e.g., background services)
            if (AmbientUserContext.Current is { } ambient)
            {
                return ambient.CurrentUserId;
            }
            
            var httpContext = httpContextAccessor.HttpContext;
            var user = httpContext?.User;
            
            var userIdClaim = user?.FindFirst(HeaderAuthenticationClaims.UserId);
            
            return userIdClaim is not null && Guid.TryParse(userIdClaim.Value, out var userId)
                ? userId
                : Guid.Empty;
        }
    }
    
    /// <inheritdoc />
    public bool IsAuthenticated => 
        AmbientUserContext.Current?.IsAuthenticated ?? 
        httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;
    
    /// <inheritdoc />
    public bool IsAdmin =>
        AmbientUserContext.Current?.IsAdmin ??
        httpContextAccessor.HttpContext?.User.HasClaim(HeaderAuthenticationClaims.IsAdmin, "true") == true;
    
    /// <inheritdoc />
    public User? GetCurrentUser()
    {
        // Check for ambient context override (e.g., background services)
        if (AmbientUserContext.Current is { } ambient)
        {
            return ambient.GetCurrentUser();
        }
        
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
        // Check for ambient context override (e.g., background services)
        if (AmbientUserContext.Current is { } ambient)
        {
            return ambient.GetRequiredCurrentUser();
        }
        
        return GetCurrentUser() 
            ?? throw new InvalidOperationException("No authenticated user in the current context.");
    }
}
