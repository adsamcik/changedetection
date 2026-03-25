using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB repository for the notification outbox.
/// Provides reliable delivery semantics with retry support.
/// All operations are serialized through <see cref="ThreadSafeLiteDbContext"/>.
/// </summary>
public class NotificationOutboxRepository : INotificationOutboxRepository
{
    private readonly ThreadSafeLiteDbContext _safeContext;
    private const string CollectionName = "notification_outbox";

    public NotificationOutboxRepository(ThreadSafeLiteDbContext safeContext)
    {
        _safeContext = safeContext;
    }

    private ILiteCollection<NotificationOutboxEntry> Col(ILiteDatabase db) =>
        db.GetCollection<NotificationOutboxEntry>(CollectionName);

    public async Task<NotificationOutboxEntry> AddAsync(NotificationOutboxEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        if (entry.Id == Guid.Empty)
            entry.Id = Guid.NewGuid();
        
        await _safeContext.ExecuteAsync(db => { Col(db).Insert(entry); }, ct);
        return entry;
    }

    public async Task<IReadOnlyList<NotificationOutboxEntry>> GetPendingAsync(int maxCount = 100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        return await _safeContext.ExecuteAsync(db =>
            (IReadOnlyList<NotificationOutboxEntry>)Col(db).Query()
                .Where(x => x.Status == NotificationStatus.Pending)
                .OrderBy(x => x.CreatedAt)
                .Limit(maxCount)
                .ToList(), ct);
    }

    public async Task<IReadOnlyList<NotificationOutboxEntry>> GetReadyForRetryAsync(int maxCount = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var now = DateTime.UtcNow;
        return await _safeContext.ExecuteAsync(db =>
            (IReadOnlyList<NotificationOutboxEntry>)Col(db).Query()
                .Where(x => x.Status == NotificationStatus.RetryPending && x.NextRetryAt <= now)
                .OrderBy(x => x.NextRetryAt)
                .Limit(maxCount)
                .ToList(), ct);
    }

    public async Task<bool> TryClaimForProcessingAsync(Guid entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        // Global semaphore in ThreadSafeLiteDbContext provides exclusive access
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var entry = col.FindById(entryId);
            if (entry == null)
                return false;
            
            if (entry.Status != NotificationStatus.Pending && entry.Status != NotificationStatus.RetryPending)
                return false;
            
            entry.Status = NotificationStatus.Processing;
            entry.ProcessingStartedAt = DateTime.UtcNow;
            col.Update(entry);
            
            return true;
        }, ct);
    }

    public async Task MarkSentAsync(Guid entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var entry = col.FindById(entryId);
            if (entry == null)
                return;
            
            entry.Status = NotificationStatus.Sent;
            entry.SentAt = DateTime.UtcNow;
            entry.LastError = null;
            col.Update(entry);
        }, ct);
    }

    public async Task MarkFailedAsync(Guid entryId, string errorMessage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var entry = col.FindById(entryId);
            if (entry == null)
                return;
            
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
                var delayMinutes = Math.Pow(2, entry.RetryCount - 1);
                entry.NextRetryAt = DateTime.UtcNow.AddMinutes(delayMinutes);
            }
            
            col.Update(entry);
        }, ct);
    }

    public async Task MarkPermanentlyFailedAsync(Guid entryId, string errorMessage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var entry = col.FindById(entryId);
            if (entry == null)
                return;
            
            entry.Status = NotificationStatus.Failed;
            entry.LastError = errorMessage;
            entry.NextRetryAt = null;
            col.Update(entry);
        }, ct);
    }

    public async Task<int> RecoverStaleProcessingAsync(TimeSpan processingTimeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        // Global semaphore provides exclusive access — no local lock needed
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var cutoff = DateTime.UtcNow - processingTimeout;
            var staleEntries = col.Query()
                .Where(x => x.Status == NotificationStatus.Processing && x.ProcessingStartedAt < cutoff)
                .ToList();
            
            foreach (var entry in staleEntries)
            {
                entry.Status = NotificationStatus.RetryPending;
                entry.LastError = "Processing timed out - recovered after restart";
                entry.NextRetryAt = DateTime.UtcNow;
                col.Update(entry);
            }
            
            return staleEntries.Count;
        }, ct);
    }

    public async Task<NotificationOutboxEntry?> GetByIdAsync(Guid entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db => Col(db).FindById(entryId), ct);
    }

    public async Task<int> DeleteOldSentAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var cutoff = DateTime.UtcNow - olderThan;
        return await _safeContext.ExecuteAsync(db =>
            Col(db).DeleteMany(x => x.Status == NotificationStatus.Sent && x.SentAt < cutoff), ct);
    }

    public async Task<NotificationOutboxStats> GetStatsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var yesterday = DateTime.UtcNow.AddDays(-1);
            
            return new NotificationOutboxStats(
                PendingCount: col.Count(x => x.Status == NotificationStatus.Pending),
                ProcessingCount: col.Count(x => x.Status == NotificationStatus.Processing),
                RetryPendingCount: col.Count(x => x.Status == NotificationStatus.RetryPending),
                FailedCount: col.Count(x => x.Status == NotificationStatus.Failed),
                SentLast24Hours: col.Count(x => x.Status == NotificationStatus.Sent && x.SentAt >= yesterday)
            );
        }, ct);
    }
}
