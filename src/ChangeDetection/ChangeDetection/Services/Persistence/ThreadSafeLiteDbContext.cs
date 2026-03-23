using LiteDB;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// Thread-safe wrapper around LiteDbContext that serializes all database operations.
/// LiteDB's Shared connection mode is NOT safe for concurrent writers from multiple threads.
/// This wrapper uses a SemaphoreSlim(1,1) to ensure exclusive access for all operations.
/// </summary>
public class ThreadSafeLiteDbContext : IDisposable
{
    private readonly LiteDbContext _inner;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<ThreadSafeLiteDbContext>? _logger;
    private bool _disposed;

    private static readonly TimeSpan SemaphoreTimeout = TimeSpan.FromSeconds(10);

    public ThreadSafeLiteDbContext(LiteDbContext inner, ILogger<ThreadSafeLiteDbContext>? logger = null)
    {
        _inner = inner;
        _logger = logger;
    }

    /// <summary>
    /// Executes a synchronous operation against the database with exclusive access.
    /// </summary>
    public T Execute<T>(Func<ILiteDatabase, T> operation)
    {
        if (!_semaphore.Wait(SemaphoreTimeout))
        {
            throw new TimeoutException(
                $"ThreadSafeLiteDbContext: Failed to acquire database semaphore within {SemaphoreTimeout.TotalSeconds}s. " +
                "Another operation may be holding the lock. This indicates a potential deadlock.");
        }
        try
        {
            return operation(_inner.Database);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Executes a synchronous void operation against the database with exclusive access.
    /// </summary>
    public void Execute(Action<ILiteDatabase> operation)
    {
        if (!_semaphore.Wait(SemaphoreTimeout))
        {
            throw new TimeoutException(
                $"ThreadSafeLiteDbContext: Failed to acquire database semaphore within {SemaphoreTimeout.TotalSeconds}s. " +
                "Another operation may be holding the lock. This indicates a potential deadlock.");
        }
        try
        {
            operation(_inner.Database);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Executes an operation against the database with exclusive access (async semaphore acquisition).
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<ILiteDatabase, T> operation, CancellationToken ct = default)
    {
        _logger?.LogDebug("ThreadSafeLiteDbContext: waiting for semaphore (async<T>)...");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SemaphoreTimeout);

        try
        {
            await _semaphore.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogWarning(
                "ThreadSafeLiteDbContext: Semaphore acquisition timed out after {Timeout}s — possible deadlock",
                SemaphoreTimeout.TotalSeconds);
            throw new TimeoutException(
                $"ThreadSafeLiteDbContext: Failed to acquire database semaphore within {SemaphoreTimeout.TotalSeconds}s. " +
                "Another operation may be holding the lock. This indicates a potential deadlock.");
        }

        _logger?.LogDebug("ThreadSafeLiteDbContext: semaphore acquired");
        try
        {
            return operation(_inner.Database);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Executes a void operation against the database with exclusive access (async semaphore acquisition).
    /// </summary>
    public async Task ExecuteAsync(Action<ILiteDatabase> operation, CancellationToken ct = default)
    {
        _logger?.LogDebug("ThreadSafeLiteDbContext: waiting for semaphore (async void)...");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SemaphoreTimeout);

        try
        {
            await _semaphore.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogWarning(
                "ThreadSafeLiteDbContext: Semaphore acquisition timed out after {Timeout}s — possible deadlock",
                SemaphoreTimeout.TotalSeconds);
            throw new TimeoutException(
                $"ThreadSafeLiteDbContext: Failed to acquire database semaphore within {SemaphoreTimeout.TotalSeconds}s. " +
                "Another operation may be holding the lock. This indicates a potential deadlock.");
        }

        _logger?.LogDebug("ThreadSafeLiteDbContext: semaphore acquired");
        try
        {
            operation(_inner.Database);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Direct access to the underlying database for backward compatibility.
    /// Prefer Execute/ExecuteAsync methods for thread-safe access.
    /// </summary>
    public ILiteDatabase UnsafeDatabase => _inner.Database;

    public void Dispose()
    {
        if (!_disposed)
        {
            _semaphore.Dispose();
            // Don't dispose _inner — it's owned by the DI container
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
