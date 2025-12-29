using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// Provides ambient (thread-local) user context that can be overridden for background services.
/// This allows background services to set their own user context that will be used by all
/// services within that async scope, regardless of how they were constructed.
/// </summary>
public static class AmbientUserContext
{
    private static readonly AsyncLocal<IUserContext?> _current = new();
    
    /// <summary>
    /// Gets the current ambient user context, if one is set.
    /// </summary>
    public static IUserContext? Current => _current.Value;
    
    /// <summary>
    /// Sets the ambient user context for the current async scope.
    /// Returns a disposable that restores the previous context when disposed.
    /// </summary>
    public static IDisposable Use(IUserContext context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new ContextScope(previous);
    }
    
    private sealed class ContextScope(IUserContext? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}

/// <summary>
/// User context implementation that checks for an ambient override before falling back to the injected context.
/// </summary>
/// <remarks>
/// This allows background services to override the user context for their scope without
/// needing to rebuild the entire service graph. Services that depend on IUserContext will
/// get this implementation, which checks for an ambient override first.
/// </remarks>
public class AmbientAwareUserContext(IUserContext? fallbackContext = null) : IUserContext
{
    /// <summary>
    /// Gets the effective user context - ambient if set, otherwise the fallback.
    /// </summary>
    private IUserContext EffectiveContext => AmbientUserContext.Current ?? fallbackContext ?? throw new InvalidOperationException("No user context available");
    
    /// <inheritdoc />
    public Guid CurrentUserId => EffectiveContext.CurrentUserId;
    
    /// <inheritdoc />
    public bool IsAuthenticated => EffectiveContext.IsAuthenticated;
    
    /// <inheritdoc />
    public bool IsAdmin => EffectiveContext.IsAdmin;
    
    /// <inheritdoc />
    public User? GetCurrentUser() => EffectiveContext.GetCurrentUser();
    
    /// <inheritdoc />
    public User GetRequiredCurrentUser() => EffectiveContext.GetRequiredCurrentUser();
}
