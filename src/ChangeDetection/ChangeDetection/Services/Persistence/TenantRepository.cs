using System.Linq.Expressions;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// Repository wrapper that applies tenant filtering to all operations.
/// For owned entities, queries are filtered by the current user's OwnerId.
/// Admins can also see orphaned records (OwnerId = Guid.Empty) from single-user mode.
/// </summary>
public class TenantRepository<T>(
    IRepository<T> innerRepository,
    IUserContext userContext) : IRepository<T> 
    where T : class, IOwnedEntity
{
    /// <summary>
    /// Gets whether the entity should be visible to the current user.
    /// Admins see ALL entities (for background service and admin dashboard).
    /// Regular users only see their own entities.
    /// </summary>
    private bool IsVisibleToCurrentUser(T entity)
    {
        // Admins can see all entities
        if (userContext.IsAdmin)
        {
            return true;
        }
        
        var userId = userContext.CurrentUserId;
        
        // User's own entities
        return entity.OwnerId == userId;
    }
    
    /// <summary>
    /// Creates a predicate that filters by OwnerId.
    /// </summary>
    private Expression<Func<T, bool>> CreateOwnerFilter()
    {
        // Admins see everything (for background services and admin views)
        if (userContext.IsAdmin)
        {
            return e => true;
        }
        
        var userId = userContext.CurrentUserId;
        
        // Regular users only see their own
        return e => e.OwnerId == userId;
    }
    
    /// <summary>
    /// Combines the owner filter with an additional predicate.
    /// Uses parameter replacement instead of Expression.Invoke for LiteDB compatibility.
    /// </summary>
    private Expression<Func<T, bool>> CombineWithOwnerFilter(Expression<Func<T, bool>> predicate)
    {
        var ownerFilter = CreateOwnerFilter();
        
        // Create a shared parameter for both expressions
        var parameter = Expression.Parameter(typeof(T), "e");
        
        // Replace parameters in both expression bodies with the shared parameter
        var ownerBody = new ParameterReplacer(ownerFilter.Parameters[0], parameter)
            .Visit(ownerFilter.Body);
        var predicateBody = new ParameterReplacer(predicate.Parameters[0], parameter)
            .Visit(predicate.Body);
        
        var combined = Expression.AndAlso(ownerBody, predicateBody);
        
        return Expression.Lambda<Func<T, bool>>(combined, parameter);
    }
    
    /// <summary>
    /// Expression visitor that replaces one parameter with another.
    /// Used to combine lambda expressions with a shared parameter.
    /// </summary>
    private sealed class ParameterReplacer(
        ParameterExpression oldParam,
        ParameterExpression newParam) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == oldParam ? newParam : base.VisitParameter(node);
    }
    
    /// <summary>
    /// Sets the OwnerId on an entity before insert.
    /// </summary>
    private void SetOwner(T entity)
    {
        // In normal (non-admin) contexts, ownership is always enforced to the current user.
        // In admin/background contexts, allow the caller to explicitly set OwnerId for
        // cross-tenant operations (e.g., background checks persisting events for the watch owner).
        if (!userContext.IsAdmin || entity.OwnerId == Guid.Empty)
        {
            entity.OwnerId = userContext.CurrentUserId;
        }
    }
    
    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await innerRepository.GetByIdAsync(id, ct);
        
        if (entity is null)
        {
            return null;
        }
        
        return IsVisibleToCurrentUser(entity) ? entity : null;
    }
    
    public async Task<IEnumerable<T>> GetAllAsync(CancellationToken ct = default)
    {
        return await innerRepository.FindAsync(CreateOwnerFilter(), ct);
    }
    
    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var combined = CombineWithOwnerFilter(predicate);
        return await innerRepository.FindAsync(combined, ct);
    }
    
    public async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var combined = CombineWithOwnerFilter(predicate);
        return await innerRepository.FirstOrDefaultAsync(combined, ct);
    }
    
    public async Task<T?> FirstOrDefaultOrderedDescAsync<TKey>(
        Expression<Func<T, bool>> predicate, 
        Expression<Func<T, TKey>> orderByDesc, 
        CancellationToken ct = default)
    {
        var combined = CombineWithOwnerFilter(predicate);
        return await innerRepository.FirstOrDefaultOrderedDescAsync(combined, orderByDesc, ct);
    }
    
    public async Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var combined = CombineWithOwnerFilter(predicate);
        return await innerRepository.ExistsAsync(combined, ct);
    }
    
    public async Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
    {
        var ownerFilter = CreateOwnerFilter();
        
        if (predicate is null)
        {
            return await innerRepository.CountAsync(ownerFilter, ct);
        }
        
        var combined = CombineWithOwnerFilter(predicate);
        return await innerRepository.CountAsync(combined, ct);
    }
    
    public async Task InsertAsync(T entity, CancellationToken ct = default)
    {
        SetOwner(entity);
        await innerRepository.InsertAsync(entity, ct);
    }
    
    public async Task InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var entityList = entities.ToList();
        foreach (var entity in entityList)
        {
            SetOwner(entity);
        }
        
        await innerRepository.InsertManyAsync(entityList, ct);
    }
    
    public async Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        // Verify ownership before updating
        var existing = await innerRepository.GetByIdAsync(GetEntityId(entity), ct);
        
        if (existing is null)
        {
            throw new InvalidOperationException("Entity not found.");
        }
        
        if (!IsVisibleToCurrentUser(existing))
        {
            throw new UnauthorizedAccessException("Cannot update entity owned by another user.");
        }
        
        // Preserve original owner (don't let updates change ownership)
        entity.OwnerId = existing.OwnerId;
        
        await innerRepository.UpdateAsync(entity, ct);
    }
    
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Verify ownership before deleting
        var existing = await innerRepository.GetByIdAsync(id, ct);
        
        if (existing is null)
        {
            return; // Already deleted
        }
        
        if (!IsVisibleToCurrentUser(existing))
        {
            throw new UnauthorizedAccessException("Cannot delete entity owned by another user.");
        }
        
        await innerRepository.DeleteAsync(id, ct);
    }
    
    public async Task DeleteManyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var combined = CombineWithOwnerFilter(predicate);
        await innerRepository.DeleteManyAsync(combined, ct);
    }
    
    /// <summary>
    /// Gets the ID of an entity using reflection.
    /// Assumes the entity has a public Id property of type Guid.
    /// </summary>
    private static Guid GetEntityId(T entity)
    {
        var idProperty = typeof(T).GetProperty("Id");
        if (idProperty is null)
        {
            throw new InvalidOperationException($"Entity type {typeof(T).Name} does not have an Id property.");
        }
        
        return (Guid)(idProperty.GetValue(entity) ?? Guid.Empty);
    }
}
