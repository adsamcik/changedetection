using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.JobWatch;

/// <summary>
/// Tracks items through their lifecycle and handles cross-portal deduplication.
/// Uses identity keys from the extraction schema to match items across sources.
/// </summary>
public class ItemTrackingService(
    IRepository<TrackedItem> itemRepo,
    IAlertPolicyService alertPolicy,
    ILogger<ItemTrackingService> logger) : IItemTrackingService
{
    /// <summary>
    /// Required number of consecutive absences before confirming a listing as expired.
    /// </summary>
    private const int AbsenceThreshold = 2;

    public async Task<ItemTrackingResult> ProcessDiffAsync(
        Guid watchGroupId,
        Guid sourceWatchId,
        Guid ownerId,
        ObjectDiffResult diffResult,
        string? matchDimensionsJson,
        string? recommendation,
        CancellationToken ct)
    {
        return await ProcessDiffAsync(watchGroupId, sourceWatchId, ownerId,
            diffResult, matchDimensionsJson, recommendation, changeEventId: null, ct);
    }

    public async Task<ItemTrackingResult> ProcessDiffAsync(
        Guid watchGroupId,
        Guid sourceWatchId,
        Guid ownerId,
        ObjectDiffResult diffResult,
        string? matchDimensionsJson,
        string? recommendation,
        Guid? changeEventId,
        CancellationToken ct)
    {
        var result = new ItemTrackingResult();
        var existing = await GetItemsAsync(watchGroupId, ct);
        var existingByKey = existing.ToDictionary(l => l.IdentityKey, StringComparer.OrdinalIgnoreCase);

        // Process added items
        foreach (var added in diffResult.AddedItems)
        {
            var identityKey = BuildIdentityKey(added);
            if (string.IsNullOrWhiteSpace(identityKey)) continue;

            if (existingByKey.TryGetValue(identityKey, out var existingItem))
            {
                // Cross-portal dedup — same item found from another watch
                if (existingItem.SourceWatchId != sourceWatchId &&
                    !existingItem.AdditionalSourceWatchIds.Contains(sourceWatchId))
                {
                    existingItem.AdditionalSourceWatchIds.Add(sourceWatchId);
                    await itemRepo.UpdateAsync(existingItem, ct);
                }

                // Reset absence counter if item reappeared
                if (existingItem.ConsecutiveAbsences > 0)
                {
                    existingItem.ConsecutiveAbsences = 0;
                    await itemRepo.UpdateAsync(existingItem, ct);
                }

                result.DuplicateItems.Add(existingItem);
                continue;
            }

            var deadline = ParseDeadline(added);
            var policyResult = alertPolicy.Evaluate(matchDimensionsJson, recommendation, deadline);

            var item = new TrackedItem
            {
                OwnerId = ownerId,
                WatchGroupId = watchGroupId,
                SourceWatchId = sourceWatchId,
                IdentityKey = identityKey,
                DisplayName = GetFieldValue(added, "title"),
                DisplaySecondary = GetFieldValue(added, "company"),
                DisplayContext = GetFieldValue(added, "location"),
                Url = GetFieldValue(added, "url"),
                Deadline = deadline,
                State = TrackedItemState.New,
                AlertLevel = policyResult.AlertLevel,
                MatchDimensionsJson = matchDimensionsJson,
                Recommendation = recommendation,
                OriginChangeEventId = changeEventId,
                ExtractedDataJson = JsonSerializer.Serialize(added.Fields)
            };

            await itemRepo.InsertAsync(item, ct);
            result.NewItems.Add(item);

            logger.LogDebug(
                "New item tracked: {DisplayName} at {DisplaySecondary} — {AlertLevel}",
                item.DisplayName, item.DisplaySecondary, item.AlertLevel);
        }

        // Process removed items
        foreach (var removed in diffResult.RemovedItems)
        {
            var identityKey = BuildIdentityKey(removed);
            if (string.IsNullOrWhiteSpace(identityKey)) continue;

            if (!existingByKey.TryGetValue(identityKey, out var trackedItem)) continue;
            if (trackedItem.State is TrackedItemState.Expired or TrackedItemState.Dismissed) continue;

            trackedItem.ConsecutiveAbsences++;

            if (trackedItem.ConsecutiveAbsences >= AbsenceThreshold)
            {
                trackedItem.State = TrackedItemState.Expired;
                trackedItem.ExpiryReason = Core.Entities.ExpiryReason.RemovedFromSource;
                trackedItem.StateChangedAt = DateTime.UtcNow;
                result.ConfirmedExpired.Add(trackedItem);
                logger.LogInformation("Item confirmed expired (removed from source): {DisplayName} at {DisplaySecondary}",
                    trackedItem.DisplayName, trackedItem.DisplaySecondary);
            }
            else
            {
                result.PotentiallyExpired.Add(trackedItem);
                logger.LogDebug("Item absent ({Count}x): {DisplayName}", trackedItem.ConsecutiveAbsences, trackedItem.DisplayName);
            }

            await itemRepo.UpdateAsync(trackedItem, ct);
        }

        return result;
    }

    public async Task<IReadOnlyList<TrackedItem>> GetItemsAsync(Guid watchGroupId, CancellationToken ct)
    {
        var results = await itemRepo.FindAsync(l => l.WatchGroupId == watchGroupId, ct);
        return results.ToList();
    }

    public async Task<IReadOnlyList<TrackedItem>> GetItemsByStateAsync(
        Guid watchGroupId, TrackedItemState state, CancellationToken ct)
    {
        var results = await itemRepo.FindAsync(
            l => l.WatchGroupId == watchGroupId && l.State == state, ct);
        return results.ToList();
    }

    public async Task<bool> TransitionStateAsync(Guid listingId, TrackedItemState newState, string? reason, CancellationToken ct)
    {
        var listing = await itemRepo.GetByIdAsync(listingId, ct);
        if (listing is null) return false;

        if (!IsValidTransition(listing.State, newState))
        {
            logger.LogWarning("Invalid state transition {From}→{To} for item {Id}", listing.State, newState, listingId);
            return false;
        }

        listing.State = newState;
        listing.StateChangedAt = DateTime.UtcNow;

        switch (newState)
        {
            case TrackedItemState.Alerted:
                listing.AlertedAt = DateTime.UtcNow;
                break;
            case TrackedItemState.Seen:
                listing.SeenAt = DateTime.UtcNow;
                break;
            case TrackedItemState.Dismissed:
                listing.DismissalReason = reason;
                break;
            case TrackedItemState.Expired:
                listing.ExpiryReason ??= Core.Entities.ExpiryReason.Manual;
                break;
        }

        await itemRepo.UpdateAsync(listing, ct);
        return true;
    }

    public async Task MarkAsSeenAsync(Guid listingId, CancellationToken ct)
    {
        var listing = await itemRepo.GetByIdAsync(listingId, ct);
        if (listing is null) return;

        if (listing.State is TrackedItemState.New or TrackedItemState.Alerted)
        {
            listing.State = TrackedItemState.Seen;
            listing.SeenAt = DateTime.UtcNow;
            listing.StateChangedAt = DateTime.UtcNow;
            await itemRepo.UpdateAsync(listing, ct);
        }
    }

    public async Task<int> ExpirePassedDeadlinesAsync(Guid watchGroupId, CancellationToken ct)
    {
        var active = await itemRepo.FindAsync(
            l => l.WatchGroupId == watchGroupId
                 && l.State != TrackedItemState.Expired
                 && l.State != TrackedItemState.Dismissed, ct);

        var expiredCount = 0;
        var now = DateTime.UtcNow.Date;

        foreach (var listing in active)
        {
            if (listing.Deadline is null || listing.Deadline.Value.Date >= now) continue;

            listing.State = TrackedItemState.Expired;
            listing.ExpiryReason = Core.Entities.ExpiryReason.DeadlinePassed;
            listing.StateChangedAt = DateTime.UtcNow;
            await itemRepo.UpdateAsync(listing, ct);
            expiredCount++;
        }

        if (expiredCount > 0)
            logger.LogInformation("Expired {Count} items with passed deadlines in group {GroupId}", expiredCount, watchGroupId);

        return expiredCount;
    }

    private static bool IsValidTransition(TrackedItemState from, TrackedItemState to) => (from, to) switch
    {
        (TrackedItemState.New, TrackedItemState.Alerted) => true,
        (TrackedItemState.New, TrackedItemState.Seen) => true,
        (TrackedItemState.New, TrackedItemState.Dismissed) => true,
        (TrackedItemState.New, TrackedItemState.Expired) => true,
        (TrackedItemState.Alerted, TrackedItemState.Seen) => true,
        (TrackedItemState.Alerted, TrackedItemState.ActedOn) => true,
        (TrackedItemState.Alerted, TrackedItemState.Dismissed) => true,
        (TrackedItemState.Alerted, TrackedItemState.Expired) => true,
        (TrackedItemState.Seen, TrackedItemState.ActedOn) => true,
        (TrackedItemState.Seen, TrackedItemState.Dismissed) => true,
        (TrackedItemState.Seen, TrackedItemState.Expired) => true,
        _ => false
    };

    private static string BuildIdentityKey(ExtractedObject obj)
    {
        var title = GetFieldValue(obj, "title") ?? "";
        var company = GetFieldValue(obj, "company") ?? "";
        return $"{title.Trim().ToLowerInvariant()}|{company.Trim().ToLowerInvariant()}";
    }

    private static string? GetFieldValue(ExtractedObject obj, string fieldName)
    {
        return obj.Fields.TryGetValue(fieldName, out var value) ? value : null;
    }

    private static DateTime? ParseDeadline(ExtractedObject obj)
    {
        var deadlineStr = GetFieldValue(obj, "deadline");
        if (string.IsNullOrWhiteSpace(deadlineStr)) return null;
        return DateTime.TryParse(deadlineStr, out var parsed) ? parsed : null;
    }
}
