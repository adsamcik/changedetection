using System.Net;
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

    /// <summary>
    /// Restricts diagnostic endpoints to SSO administrators or, in single-user
    /// mode, requests originating from the local machine.
    /// </summary>
    public static RouteHandlerBuilder RequireDiagnosticAccess(
        this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            // Resolve the final, layered configuration for each request. Test hosts and
            // deployment providers can add configuration after endpoint registration.
            var configuration = context.HttpContext.RequestServices
                .GetRequiredService<IConfiguration>();
            var settings = configuration
                .GetSection(AuthenticationSettings.SectionName)
                .Get<AuthenticationSettings>() ?? new AuthenticationSettings();

            if (settings.Mode == AuthenticationMode.SSO)
            {
                var user = context.HttpContext.User;
                if (user.Identity?.IsAuthenticated != true)
                {
                    return Results.Unauthorized();
                }

                var isAdmin = user.HasClaim(HeaderAuthenticationClaims.IsAdmin, "true") ||
                              user.IsInRole(settings.AdminGroup) ||
                              user.FindAll(HeaderAuthenticationClaims.Group)
                                  .Select(claim => claim.Value)
                                  .Contains(settings.AdminGroup, StringComparer.OrdinalIgnoreCase);

                return isAdmin
                    ? await next(context)
                    : Results.Forbid();
            }

            var remoteAddress = context.HttpContext.Connection.RemoteIpAddress;
            if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
            {
                return Results.NotFound();
            }

            return await next(context);
        });
    }
}
