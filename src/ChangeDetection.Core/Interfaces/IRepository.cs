using System.Linq.Expressions;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Generic repository interface for data access.
/// </summary>
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<T>> GetAllAsync(CancellationToken ct = default);
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    
    /// <summary>
    /// Finds the first entity matching the predicate, ordered by the specified key in descending order.
    /// More efficient than FindAsync + OrderByDescending + FirstOrDefault when only one result is needed.
    /// </summary>
    Task<T?> FirstOrDefaultOrderedDescAsync<TKey>(
        Expression<Func<T, bool>> predicate, 
        Expression<Func<T, TKey>> orderByDesc, 
        CancellationToken ct = default);
    
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);
    Task InsertAsync(T entity, CancellationToken ct = default);
    Task InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task DeleteManyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
}
