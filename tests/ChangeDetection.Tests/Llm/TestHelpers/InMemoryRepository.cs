using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Tests.Llm.TestHelpers;

/// <summary>
/// Simple in-memory repository for testing.
/// </summary>
public class InMemoryRepository<T> : IRepository<T> where T : class
{
    private readonly List<T> _items = [];
    
    public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var idProp = typeof(T).GetProperty("Id");
        return Task.FromResult(_items.FirstOrDefault(x => 
            idProp?.GetValue(x)?.Equals(id) == true));
    }

    public Task<IEnumerable<T>> GetAllAsync(CancellationToken ct = default) 
        => Task.FromResult<IEnumerable<T>>(_items);

    public Task InsertAsync(T entity, CancellationToken ct = default)
    {
        _items.Add(entity);
        return Task.CompletedTask;
    }

    public Task InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        _items.AddRange(entities);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(T entity, CancellationToken ct = default) 
        => Task.CompletedTask;

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var idProp = typeof(T).GetProperty("Id");
        _items.RemoveAll(x => idProp?.GetValue(x)?.Equals(id) == true);
        return Task.CompletedTask;
    }

    public Task DeleteManyAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken ct = default)
    {
        var compiled = predicate.Compile();
        _items.RemoveAll(x => compiled(x));
        return Task.CompletedTask;
    }

    public Task<IEnumerable<T>> FindAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken ct = default)
    {
        var compiled = predicate.Compile();
        return Task.FromResult(_items.Where(compiled));
    }

    public Task<T?> FirstOrDefaultAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken ct = default)
    {
        var compiled = predicate.Compile();
        return Task.FromResult(_items.FirstOrDefault(compiled));
    }

    public Task<T?> FirstOrDefaultOrderedDescAsync<TKey>(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        System.Linq.Expressions.Expression<Func<T, TKey>> orderByDesc,
        CancellationToken ct = default)
    {
        var predicateCompiled = predicate.Compile();
        var orderCompiled = orderByDesc.Compile();
        return Task.FromResult(_items
            .Where(predicateCompiled)
            .OrderByDescending(orderCompiled)
            .FirstOrDefault());
    }

    public Task<bool> ExistsAsync(
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        CancellationToken ct = default)
    {
        var compiled = predicate.Compile();
        return Task.FromResult(_items.Any(compiled));
    }

    public Task<int> CountAsync(
        System.Linq.Expressions.Expression<Func<T, bool>>? predicate = null,
        CancellationToken ct = default)
    {
        if (predicate == null)
            return Task.FromResult(_items.Count);
        
        var compiled = predicate.Compile();
        return Task.FromResult(_items.Count(compiled));
    }
}
