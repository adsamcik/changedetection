using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// Factory for creating service scopes with background service user context.
/// Use this in background services to ensure they have admin-level access to all data.
/// </summary>
public interface IBackgroundServiceScopeFactory
{
    /// <summary>
    /// Creates a service scope with BackgroundServiceUserContext as the IUserContext.
    /// </summary>
    IServiceScope CreateBackgroundScope();
}

/// <summary>
/// Implementation of IBackgroundServiceScopeFactory that wraps the standard scope factory.
/// </summary>
public class BackgroundServiceScopeFactory(IServiceScopeFactory scopeFactory) : IBackgroundServiceScopeFactory
{
    public IServiceScope CreateBackgroundScope()
    {
        return new BackgroundServiceScope(scopeFactory.CreateScope());
    }
}

/// <summary>
/// Service scope wrapper that provides BackgroundServiceUserContext.
/// </summary>
internal class BackgroundServiceScope(IServiceScope innerScope) : IServiceScope
{
    private readonly BackgroundServiceUserContext _userContext = new();
    
    public IServiceProvider ServiceProvider => new BackgroundServiceProvider(innerScope.ServiceProvider, _userContext);
    
    public void Dispose() => innerScope.Dispose();
}

/// <summary>
/// Service provider wrapper that substitutes IUserContext with BackgroundServiceUserContext.
/// </summary>
internal class BackgroundServiceProvider(IServiceProvider innerProvider, IUserContext userContext) : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IUserContext))
        {
            return userContext;
        }
        
        return innerProvider.GetService(serviceType);
    }
}
