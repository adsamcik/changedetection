using System.Linq.Expressions;
using ChangeDetection.Core.Interfaces;
using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// Generic LiteDB repository implementation.
/// All operations are serialized through <see cref="ThreadSafeLiteDbContext"/>.
/// </summary>
public class LiteDbRepository<T> : IRepository<T> where T : class
{
    private readonly ThreadSafeLiteDbContext _safeContext;
    private readonly string _collectionName;

    public LiteDbRepository(ThreadSafeLiteDbContext safeContext, string? collectionName = null)
    {
        _safeContext = safeContext;
        _collectionName = collectionName ?? typeof(T).Name.ToLowerInvariant() + "s";
    }

    private ILiteCollection<T> Col(ILiteDatabase db) => db.GetCollection<T>(_collectionName);

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db => Col(db).FindById(id), ct);
    }

    public async Task<IEnumerable<T>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            (IEnumerable<T>)Col(db).FindAll().ToList(), ct);
    }

    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            (IEnumerable<T>)Col(db).Find(predicate).ToList(), ct);
    }

    public async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db => Col(db).FindOne(predicate), ct);
    }

    public async Task<T?> FirstOrDefaultOrderedDescAsync<TKey>(
        Expression<Func<T, bool>> predicate,
        Expression<Func<T, TKey>> orderByDesc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            Col(db).Query()
                .Where(predicate)
                .OrderByDescending(orderByDesc)
                .Limit(1)
                .FirstOrDefault(), ct);
    }

    public async Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db => Col(db).Exists(predicate), ct);
    }

    public async Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            predicate == null ? Col(db).Count() : Col(db).Count(predicate), ct);
    }

    public async Task InsertAsync(T entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db => { Col(db).Insert(entity); }, ct);
    }

    public async Task InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db => { Col(db).InsertBulk(entities); }, ct);
    }

    public async Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db => { Col(db).Update(entity); }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db => { Col(db).Delete(id); }, ct);
    }

    public async Task DeleteManyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db => { Col(db).DeleteMany(predicate); }, ct);
    }
}
