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
/// Implementation of IBackgroundServiceScopeFactory that creates scopes with BackgroundServiceUserContext.
/// </summary>
/// <remarks>
/// This implementation uses the ambient user context pattern to override IUserContext for the
/// duration of the scope. This ensures that all services within the scope see the background
/// service user context, regardless of how they were originally constructed.
/// </remarks>
public class BackgroundServiceScopeFactory(IServiceProvider rootProvider) : IBackgroundServiceScopeFactory
{
    public IServiceScope CreateBackgroundScope()
    {
        return new BackgroundServiceScope(rootProvider);
    }
}

/// <summary>
/// Service scope that provides BackgroundServiceUserContext via the ambient context.
/// </summary>
/// <remarks>
/// Uses <see cref="AmbientUserContext"/> to set the background service context for all
/// services that use <see cref="AmbientAwareUserContext"/> as their IUserContext implementation.
/// </remarks>
internal sealed class BackgroundServiceScope : IServiceScope
{
    private readonly IServiceScope _innerScope;
    private readonly IDisposable _contextOverride;
    
    public BackgroundServiceScope(IServiceProvider rootProvider)
    {
        _innerScope = rootProvider.CreateScope();
        _contextOverride = AmbientUserContext.Use(new BackgroundServiceUserContext());
    }
    
    public IServiceProvider ServiceProvider => _innerScope.ServiceProvider;
    
    public void Dispose()
    {
        _contextOverride.Dispose();
        _innerScope.Dispose();
    }
}

