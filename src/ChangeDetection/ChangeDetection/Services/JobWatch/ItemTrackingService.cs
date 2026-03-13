using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.JobWatch;

/// <summary>
/// Tracks items through their lifecycle and handles cross-source deduplication.
/// All field mapping, identity key building, and absence thresholds are driven
/// by TrackingConfig from the WatchGroup — no hard-coded domain assumptions.
/// </summary>
public class ItemTrackingService(
    IRepository<TrackedItem> itemRepo,
    IRepository<WatchGroup> groupRepo,
    IAlertPolicyService alertPolicy,
    ILogger<ItemTrackingService> logger) : IItemTrackingService
{
    private static readonly TrackingConfig DefaultConfig = TrackingConfig.ForJobs();

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
            diffResult, matchDimensionsJson, recommendation,
            changeEventId: null, schemaIdentityFields: null, ct);
    }

    public async Task<ItemTrackingResult> ProcessDiffAsync(
        Guid watchGroupId,
        Guid sourceWatchId,
        Guid ownerId,
        ObjectDiffResult diffResult,
        string? matchDimensionsJson,
        string? recommendation,
        Guid? changeEventId,
        IReadOnlyList<string>? schemaIdentityFields,
        CancellationToken ct)
    {
        var config = await ResolveConfigAsync(watchGroupId, ct);
        var result = new ItemTrackingResult();
        var existing = await GetItemsAsync(watchGroupId, ct);
        var existingByKey = existing.ToDictionary(l => l.IdentityKey, StringComparer.OrdinalIgnoreCase);

        // Process added items
        foreach (var added in diffResult.AddedItems)
        {
            var identityKey = BuildIdentityKey(added, schemaIdentityFields, config);
            if (string.IsNullOrWhiteSpace(identityKey)) continue;

            if (existingByKey.TryGetValue(identityKey, out var existingItem))
            {
                // Cross-source dedup — same item found from another watch
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

            var deadline = ParseDeadline(added, config);
            var policyResult = alertPolicy.Evaluate(matchDimensionsJson, recommendation, deadline, config);

            var item = new TrackedItem
            {
                OwnerId = ownerId,
                WatchGroupId = watchGroupId,
                SourceWatchId = sourceWatchId,
                IdentityKey = identityKey,
                DisplayName = GetFieldValue(added, config.DisplayNameField),
                DisplaySecondary = config.DisplaySecondaryField is not null
                    ? GetFieldValue(added, config.DisplaySecondaryField) : null,
                DisplayContext = config.DisplayContextField is not null
                    ? GetFieldValue(added, config.DisplayContextField) : null,
                Url = GetFieldValue(added, config.UrlField),
                Deadline = deadline,
                State = TrackedItemState.New,
                AlertLevel = policyResult.AlertLevel,
                MatchDimensionsJson = matchDimensionsJson,
                Recommendation = recommendation,
                ItemType = config.ItemType,
                OriginChangeEventId = changeEventId,
                ExtractedDataJson = JsonSerializer.Serialize(added.Fields)
            };

            await itemRepo.InsertAsync(item, ct);
            result.NewItems.Add(item);

            logger.LogDebug(
                "New {ItemType} tracked: {DisplayName} — {AlertLevel}",
                config.ItemType, item.DisplayName, item.AlertLevel);
        }

        // Process removed items
        foreach (var removed in diffResult.RemovedItems)
        {
            var identityKey = BuildIdentityKey(removed, schemaIdentityFields, config);
            if (string.IsNullOrWhiteSpace(identityKey)) continue;

            if (!existingByKey.TryGetValue(identityKey, out var trackedItem)) continue;
            if (trackedItem.State is TrackedItemState.Expired or TrackedItemState.Dismissed) continue;

            trackedItem.ConsecutiveAbsences++;

            if (trackedItem.ConsecutiveAbsences >= config.AbsenceThreshold)
            {
                trackedItem.State = TrackedItemState.Expired;
                trackedItem.ExpiryReason = ExpiryReason.RemovedFromSource;
                trackedItem.StateChangedAt = DateTime.UtcNow;
                result.ConfirmedExpired.Add(trackedItem);
                logger.LogInformation("Item confirmed expired (removed from source): {DisplayName}",
                    trackedItem.DisplayName);
            }
            else
            {
                result.PotentiallyExpired.Add(trackedItem);
                logger.LogDebug("Item absent ({Count}/{Threshold}): {DisplayName}",
                    trackedItem.ConsecutiveAbsences, config.AbsenceThreshold, trackedItem.DisplayName);
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

    public async Task<bool> TransitionStateAsync(Guid itemId, TrackedItemState newState, string? reason, CancellationToken ct)
    {
        var item = await itemRepo.GetByIdAsync(itemId, ct);
        if (item is null) return false;

        if (!IsValidTransition(item.State, newState))
        {
            logger.LogWarning("Invalid state transition {From}→{To} for item {Id}", item.State, newState, itemId);
            return false;
        }

        item.State = newState;
        item.StateChangedAt = DateTime.UtcNow;

        switch (newState)
        {
            case TrackedItemState.Alerted:
                item.AlertedAt = DateTime.UtcNow;
                break;
            case TrackedItemState.Seen:
                item.SeenAt = DateTime.UtcNow;
                break;
            case TrackedItemState.Dismissed:
                item.DismissalReason = reason;
                break;
            case TrackedItemState.Expired:
                item.ExpiryReason ??= ExpiryReason.Manual;
                break;
        }

        await itemRepo.UpdateAsync(item, ct);
        return true;
    }

    public async Task MarkAsSeenAsync(Guid itemId, CancellationToken ct)
    {
        var item = await itemRepo.GetByIdAsync(itemId, ct);
        if (item is null) return;

        if (item.State is TrackedItemState.New or TrackedItemState.Alerted)
        {
            item.State = TrackedItemState.Seen;
            item.SeenAt = DateTime.UtcNow;
            item.StateChangedAt = DateTime.UtcNow;
            await itemRepo.UpdateAsync(item, ct);
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

        foreach (var item in active)
        {
            if (item.Deadline is null || item.Deadline.Value.Date >= now) continue;

            item.State = TrackedItemState.Expired;
            item.ExpiryReason = ExpiryReason.DeadlinePassed;
            item.StateChangedAt = DateTime.UtcNow;
            await itemRepo.UpdateAsync(item, ct);
            expiredCount++;
        }

        if (expiredCount > 0)
            logger.LogInformation("Expired {Count} items with passed deadlines in group {GroupId}", expiredCount, watchGroupId);

        return expiredCount;
    }

    private async Task<TrackingConfig> ResolveConfigAsync(Guid watchGroupId, CancellationToken ct)
    {
        var group = await groupRepo.GetByIdAsync(watchGroupId, ct);
        return group?.TrackingConfig ?? DefaultConfig;
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

    /// <summary>
    /// Builds identity key using schema identity fields when available,
    /// falling back to TrackingConfig display fields, then to the object's own IdentityKey.
    /// </summary>
    private static string BuildIdentityKey(
        ExtractedObject obj,
        IReadOnlyList<string>? schemaIdentityFields,
        TrackingConfig config)
    {
        // Priority 1: Use the object's pre-computed identity key from ObjectDiffService
        if (!string.IsNullOrWhiteSpace(obj.IdentityKey))
            return obj.IdentityKey.Trim().ToLowerInvariant();

        // Priority 2: Use schema-defined identity fields
        if (schemaIdentityFields is { Count: > 0 })
        {
            var parts = schemaIdentityFields
                .Select(f => (GetFieldValue(obj, f) ?? "").Trim().ToLowerInvariant())
                .Where(p => p.Length > 0);
            var key = string.Join("|", parts);
            if (key.Length > 0) return key;
        }

        // Priority 3: Build from config display fields
        var name = (GetFieldValue(obj, config.DisplayNameField) ?? "").Trim().ToLowerInvariant();
        var secondary = config.DisplaySecondaryField is not null
            ? (GetFieldValue(obj, config.DisplaySecondaryField) ?? "").Trim().ToLowerInvariant()
            : "";
        return secondary.Length > 0 ? $"{name}|{secondary}" : name;
    }

    private static string? GetFieldValue(ExtractedObject obj, string fieldName)
    {
        return obj.Fields.TryGetValue(fieldName, out var value) ? value : null;
    }

    private static DateTime? ParseDeadline(ExtractedObject obj, TrackingConfig config)
    {
        if (config.DeadlineField is null) return null;
        var deadlineStr = GetFieldValue(obj, config.DeadlineField);
        if (string.IsNullOrWhiteSpace(deadlineStr)) return null;
        return DateTime.TryParse(deadlineStr, out var parsed) ? parsed : null;
    }
}
