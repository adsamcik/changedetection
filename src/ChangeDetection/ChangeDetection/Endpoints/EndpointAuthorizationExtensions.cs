using ChangeDetection.Services.Authentication;
using ChangeDetection.Core.Entities;

namespace ChangeDetection.Endpoints;

/// <summary>
/// Extension methods for applying authorization to endpoint groups and hubs.
/// </summary>
public static class EndpointAuthorizationExtensions
{
    /// <summary>
    /// Applies authentication requirement to the endpoint group based on auth mode.
    /// In SSO mode, all endpoints require authentication.
    /// In SingleUser mode, no authentication is required.
    /// </summary>
    public static RouteGroupBuilder RequireAuthenticationInSsoMode(
        this RouteGroupBuilder group,
        IConfiguration configuration)
    {
        var settings = configuration
            .GetSection(AuthenticationSettings.SectionName)
            .Get<AuthenticationSettings>() ?? new AuthenticationSettings();

        if (settings.Mode == AuthenticationMode.SSO)
        {
            group.RequireAuthorization(AuthorizationPolicies.Authenticated);
        }
        
        return group;
    }
    
    /// <summary>
    /// Applies admin authorization requirement to the endpoint group.
    /// In SSO mode, requires admin group membership.
    /// In SingleUser mode, all users are admin.
    /// </summary>
    public static RouteGroupBuilder RequireAdminInSsoMode(
        this RouteGroupBuilder group,
        IConfiguration configuration)
    {
        var settings = configuration
            .GetSection(AuthenticationSettings.SectionName)
            .Get<AuthenticationSettings>() ?? new AuthenticationSettings();

        if (settings.Mode == AuthenticationMode.SSO)
        {
            group.RequireAuthorization(AuthorizationPolicies.Admin);
        }
        
        return group;
    }

    /// <summary>
    /// Applies authentication requirement to a SignalR hub based on auth mode.
    /// In SSO mode, hub connections require authentication.
    /// In SingleUser mode, no authentication is required.
    /// </summary>
    public static HubEndpointConventionBuilder RequireAuthenticationInSsoMode(
        this HubEndpointConventionBuilder builder,
        IConfiguration configuration)
    {
        var settings = configuration
            .GetSection(AuthenticationSettings.SectionName)
            .Get<AuthenticationSettings>() ?? new AuthenticationSettings();

        if (settings.Mode == AuthenticationMode.SSO)
        {
            builder.RequireAuthorization(AuthorizationPolicies.Authenticated);
        }
        
        return builder;
    }
}
