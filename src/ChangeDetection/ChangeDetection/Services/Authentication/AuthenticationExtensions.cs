using System.Net;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// Extension methods for configuring authentication services.
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Adds authentication and authorization services based on the configured mode.
    /// </summary>
    public static IServiceCollection AddChangeDetectionAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind authentication settings
        var authSection = configuration.GetSection(AuthenticationSettings.SectionName);
        services.Configure<AuthenticationSettings>(authSection);
        
        var settings = authSection.Get<AuthenticationSettings>() ?? new AuthenticationSettings();
        
        // Register background service scope factory (used by background services to get admin access)
        services.AddSingleton<IBackgroundServiceScopeFactory, BackgroundServiceScopeFactory>();
        
        // Always register authentication services to ensure middleware can be activated.
        // This is required for WebApplicationFactory tests that may override config to SSO mode.
        // The middleware itself is only added conditionally in UseChangeDetectionAuthentication.
        services.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                HeaderAuthenticationHandler.SchemeName, 
                null);
        
        if (settings.Mode == AuthenticationMode.SSO)
        {
            // SSO mode: Use header-based user context
            services.AddScoped<IUserContext, SsoUserContext>();
            
            // Register user service for auto-provisioning
            services.AddScoped<IUserService, UserService>();
            
            // Register user repository
            services.AddScoped<IRepository<User>>(sp => 
                new LiteDbRepository<User>(sp.GetRequiredService<ThreadSafeLiteDbContext>(), "users"));
        }
        else
        {
            // Single-user mode: No authentication needed
            services.AddSingleton<IUserContext, SingleUserContext>();
            
            // No user service needed in single-user mode, but register a stub for DI
            services.AddSingleton<IUserService, SingleUserModeUserService>();
        }
        
        // Configure authorization policies
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.Admin, policy =>
            {
                if (settings.Mode == AuthenticationMode.SSO)
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new AdminRequirement());
                }
                else
                {
                    // In single-user mode, everyone is admin
                    policy.RequireAssertion(_ => true);
                }
            })
            .AddPolicy(AuthorizationPolicies.Authenticated, policy =>
            {
                if (settings.Mode == AuthenticationMode.SSO)
                {
                    policy.RequireAuthenticatedUser();
                }
                else
                {
                    // In single-user mode, everyone is authenticated
                    policy.RequireAssertion(_ => true);
                }
            });
        
        // Register admin authorization handler
        services.AddSingleton<IAuthorizationHandler, AdminRequirementHandler>();
        
        // Configure forwarded headers for reverse proxy support
        ConfigureForwardedHeaders(services, settings);
        
        return services;
    }
    
    /// <summary>
    /// Configures forwarded headers with proper proxy trust validation.
    /// 
    /// SECURITY: This method implements defense-in-depth for header injection attacks:
    /// - By default, only loopback addresses are trusted
    /// - TrustedProxies must be explicitly configured for reverse proxy deployments
    /// </summary>
    private static void ConfigureForwardedHeaders(IServiceCollection services, AuthenticationSettings settings)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | 
                                       ForwardedHeaders.XForwardedProto |
                                       ForwardedHeaders.XForwardedHost;

            if (settings.TrustedProxies.Count > 0)
            {
                // Clear defaults and add only explicitly trusted proxies
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                
                foreach (var proxy in settings.TrustedProxies)
                {
                    if (string.IsNullOrWhiteSpace(proxy))
                    {
                        continue;
                    }
                    
                    // Check if it's a CIDR notation (contains /)
                    if (proxy.Contains('/'))
                    {
                        if (TryParseCidr(proxy, out var network))
                        {
                            options.KnownIPNetworks.Add(network);
                        }
                    }
                    else
                    {
                        // Single IP address
                        if (IPAddress.TryParse(proxy, out var ipAddress))
                        {
                            options.KnownProxies.Add(ipAddress);
                        }
                    }
                }
            }
            // If no TrustedProxies are configured,
            // keep the default behavior which only trusts loopback addresses.
            // This is the most secure default.
        });
    }
    
    /// <summary>
    /// Attempts to parse a CIDR notation string into an IPNetwork.
    /// </summary>
    /// <param name="cidr">The CIDR notation string (e.g., "10.0.0.0/8" or "fd00::/8").</param>
    /// <param name="network">The parsed IPNetwork if successful.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    private static bool TryParseCidr(string cidr, out System.Net.IPNetwork network)
    {
        network = default!;
        
        var parts = cidr.Split('/');
        if (parts.Length != 2)
        {
            return false;
        }
        
        if (!IPAddress.TryParse(parts[0], out var baseAddress))
        {
            return false;
        }
        
        if (!int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }
        
        // Validate prefix length based on address family
        var maxPrefixLength = baseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            return false;
        }
        
        network = new System.Net.IPNetwork(baseAddress, prefixLength);
        return true;
    }
    
    /// <summary>
    /// Configures the authentication and authorization middleware pipeline.
    /// Note: UseForwardedHeaders should be called separately and BEFORE UseHttpsRedirection in Program.cs.
    /// </summary>
    public static IApplicationBuilder UseChangeDetectionAuthentication(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        var settings = configuration
            .GetSection(AuthenticationSettings.SectionName)
            .Get<AuthenticationSettings>() ?? new AuthenticationSettings();
        
        if (settings.Mode == AuthenticationMode.SSO)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }
        
        return app;
    }
}

/// <summary>
/// Stub user service for single-user mode.
/// Most operations are not needed but interface must be satisfied for DI.
/// </summary>
internal class SingleUserModeUserService : IUserService
{
    private static readonly User DefaultUser = new()
    {
        Id = Guid.Empty,
        Username = "default",
        DisplayName = "Default User",
        Groups = ["admin"]
    };
    
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<User?>(id == Guid.Empty ? DefaultUser : null);
    
    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
        => Task.FromResult<User?>(string.Equals(username, "default", StringComparison.OrdinalIgnoreCase) 
            ? DefaultUser 
            : null);
    
    public Task<User> GetOrCreateFromSsoAsync(
        string username, string? email, string? displayName, 
        IReadOnlyList<string> groups, CancellationToken ct = default)
        => Task.FromResult(DefaultUser);
    
    public Task<IEnumerable<User>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IEnumerable<User>>([DefaultUser]);
    
    public Task UpdateAsync(User user, CancellationToken ct = default)
        => Task.CompletedTask;
    
    public Task DeactivateAsync(Guid userId, CancellationToken ct = default)
        => Task.CompletedTask;
    
    public Task ReactivateAsync(Guid userId, CancellationToken ct = default)
        => Task.CompletedTask;
}
