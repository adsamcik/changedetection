using System.Linq.Expressions;
using ChangeDetection.Core.Interfaces;
using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// Generic LiteDB repository implementation.
/// </summary>
public class LiteDbRepository<T> : IRepository<T> where T : class
{
    private readonly ILiteCollection<T> _collection;

    public LiteDbRepository(LiteDbContext context, string? collectionName = null)
    {
        var name = collectionName ?? typeof(T).Name.ToLowerInvariant() + "s";
        _collection = context.Database.GetCollection<T>(name);
    }

    public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = _collection.FindById(id);
        return Task.FromResult<T?>(result);
    }

    public Task<IEnumerable<T>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); 
        var results = _collection.FindAll().ToList();
        return Task.FromResult<IEnumerable<T>>(results);
    }

    public Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var results = _collection.Find(predicate).ToList();
        return Task.FromResult<IEnumerable<T>>(results);
    }

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = _collection.FindOne(predicate);
        return Task.FromResult<T?>(result);
    }

    public Task<T?> FirstOrDefaultOrderedDescAsync<TKey>(
        Expression<Func<T, bool>> predicate, 
        Expression<Func<T, TKey>> orderByDesc, 
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Use LiteDB Query API for efficient ordering with limit
        var result = _collection.Query()
            .Where(predicate)
            .OrderByDescending(orderByDesc)
            .Limit(1)
            .FirstOrDefault();
        return Task.FromResult<T?>(result);
    }

    public Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var exists = _collection.Exists(predicate);
        return Task.FromResult(exists);
    }

    public Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var count = predicate == null ? _collection.Count() : _collection.Count(predicate);
        return Task.FromResult(count);
    }

    public Task InsertAsync(T entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collection.Insert(entity);
        return Task.CompletedTask;
    }

    public Task InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collection.InsertBulk(entities);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collection.Update(entity);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collection.Delete(id);
        return Task.CompletedTask;
    }

    public Task DeleteManyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collection.DeleteMany(predicate);
        return Task.CompletedTask;
    }
}
