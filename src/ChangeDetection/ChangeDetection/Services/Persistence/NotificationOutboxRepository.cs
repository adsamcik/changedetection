using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB repository for the notification outbox.
/// Provides reliable delivery semantics with retry support.
/// </summary>
public class NotificationOutboxRepository(LiteDbContext context) : INotificationOutboxRepository
{
    private readonly ILiteCollection<NotificationOutboxEntry> _collection = InitializeCollection(context);
    private readonly object _claimLock = new();

    private static ILiteCollection<NotificationOutboxEntry> InitializeCollection(LiteDbContext context)
    {
        var collection = context.Database.GetCollection<NotificationOutboxEntry>("notification_outbox");
        
        // Indexes for efficient queries
        collection.EnsureIndex(x => x.Status);
        collection.EnsureIndex(x => x.CreatedAt);
        collection.EnsureIndex(x => x.NextRetryAt);
        collection.EnsureIndex(x => x.ProcessingStartedAt);
        collection.EnsureIndex(x => x.WatchedSiteId);
        
        return collection;
    }

    public Task<NotificationOutboxEntry> AddAsync(NotificationOutboxEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        if (entry.Id == Guid.Empty)
            entry.Id = Guid.NewGuid();
        
        _collection.Insert(entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<NotificationOutboxEntry>> GetPendingAsync(int maxCount = 100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var results = _collection.Query()
            .Where(x => x.Status == NotificationStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .Limit(maxCount)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<NotificationOutboxEntry>>(results);
    }

    public Task<IReadOnlyList<NotificationOutboxEntry>> GetReadyForRetryAsync(int maxCount = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var now = DateTime.UtcNow;
        var results = _collection.Query()
            .Where(x => x.Status == NotificationStatus.RetryPending && x.NextRetryAt <= now)
            .OrderBy(x => x.NextRetryAt)
            .Limit(maxCount)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<NotificationOutboxEntry>>(results);
    }

    public Task<bool> TryClaimForProcessingAsync(Guid entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        lock (_claimLock)
        {
            var entry = _collection.FindById(entryId);
            if (entry == null)
                return Task.FromResult(false);
            
            // Only claim if still in a claimable state
            if (entry.Status != NotificationStatus.Pending && entry.Status != NotificationStatus.RetryPending)
                return Task.FromResult(false);
            
            entry.Status = NotificationStatus.Processing;
            entry.ProcessingStartedAt = DateTime.UtcNow;
            _collection.Update(entry);
            
            return Task.FromResult(true);
        }
    }

    public Task MarkSentAsync(Guid entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var entry = _collection.FindById(entryId);
        if (entry == null)
            return Task.CompletedTask;
        
        entry.Status = NotificationStatus.Sent;
        entry.SentAt = DateTime.UtcNow;
        entry.LastError = null;
        _collection.Update(entry);
        
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid entryId, string errorMessage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var entry = _collection.FindById(entryId);
        if (entry == null)
            return Task.CompletedTask;
        
        entry.RetryCount++;
        entry.LastError = errorMessage;
        
        if (entry.RetryCount >= entry.MaxRetries)
        {
            entry.Status = NotificationStatus.Failed;
            entry.NextRetryAt = null;
        }
        else
        {
            entry.Status = NotificationStatus.RetryPending;
            // Exponential backoff: 1min, 2min, 4min, 8min, 16min
            var delayMinutes = Math.Pow(2, entry.RetryCount - 1);
            entry.NextRetryAt = DateTime.UtcNow.AddMinutes(delayMinutes);
        }
        
        _collection.Update(entry);
        return Task.CompletedTask;
    }

    public Task MarkPermanentlyFailedAsync(Guid entryId, string errorMessage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var entry = _collection.FindById(entryId);
        if (entry == null)
            return Task.CompletedTask;
        
        entry.Status = NotificationStatus.Failed;
        entry.LastError = errorMessage;
        entry.NextRetryAt = null;
        _collection.Update(entry);
        
        return Task.CompletedTask;
    }

    public Task<int> RecoverStaleProcessingAsync(TimeSpan processingTimeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        lock (_claimLock)
        {
            var cutoff = DateTime.UtcNow - processingTimeout;
            var staleEntries = _collection.Query()
                .Where(x => x.Status == NotificationStatus.Processing && x.ProcessingStartedAt < cutoff)
                .ToList();
            
            foreach (var entry in staleEntries)
            {
                entry.Status = NotificationStatus.RetryPending;
                entry.LastError = "Processing timed out - recovered after restart";
                entry.NextRetryAt = DateTime.UtcNow;
                _collection.Update(entry);
            }
            
            return Task.FromResult(staleEntries.Count);
        }
    }

    public Task<NotificationOutboxEntry?> GetByIdAsync(Guid entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<NotificationOutboxEntry?>(_collection.FindById(entryId));
    }

    public Task<int> DeleteOldSentAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var cutoff = DateTime.UtcNow - olderThan;
        var deletedCount = _collection.DeleteMany(x => 
            x.Status == NotificationStatus.Sent && x.SentAt < cutoff);
        
        return Task.FromResult(deletedCount);
    }

    public Task<NotificationOutboxStats> GetStatsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var yesterday = DateTime.UtcNow.AddDays(-1);
        
        var stats = new NotificationOutboxStats(
            PendingCount: _collection.Count(x => x.Status == NotificationStatus.Pending),
            ProcessingCount: _collection.Count(x => x.Status == NotificationStatus.Processing),
            RetryPendingCount: _collection.Count(x => x.Status == NotificationStatus.RetryPending),
            FailedCount: _collection.Count(x => x.Status == NotificationStatus.Failed),
            SentLast24Hours: _collection.Count(x => x.Status == NotificationStatus.Sent && x.SentAt >= yesterday)
        );
        
        return Task.FromResult(stats);
    }
}
