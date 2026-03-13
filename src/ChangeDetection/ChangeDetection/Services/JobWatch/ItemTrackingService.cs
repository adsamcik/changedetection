using System.Globalization;
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
    IRepository<WatchedSite> watchRepo,
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
        var sourceWatchUrl = await ResolveWatchUrlAsync(sourceWatchId, ct);
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
                Url = NormalizeUrl(GetFieldValue(added, config.UrlField), sourceWatchUrl),
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

        // Process removed items — with minimum-item guard to prevent mass expiry
        // from broken fetches (F3: partial selector match returning 0 real items)
        var totalCurrentItems = diffResult.AddedItems.Count +
            existing.Count(e => !diffResult.RemovedItems.Any(r =>
                BuildIdentityKey(r, schemaIdentityFields, config)
                    .Equals(e.IdentityKey, StringComparison.OrdinalIgnoreCase)));

        var processRemovals = true;
        if (diffResult.RemovedItems.Count > 0 && totalCurrentItems == 0 && existing.Count > config.MinimumItemThreshold)
        {
            logger.LogWarning(
                "Skipping removal processing: 0 current items but {ExistingCount} tracked items " +
                "(exceeds MinimumItemThreshold={Threshold}). Likely extraction failure, not mass removal.",
                existing.Count, config.MinimumItemThreshold);
            processRemovals = false;
        }

        if (processRemovals)
        {
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

    private async Task<string?> ResolveWatchUrlAsync(Guid watchId, CancellationToken ct)
    {
        var watch = await watchRepo.GetByIdAsync(watchId, ct);
        return watch?.Url;
    }

    /// <summary>
    /// Resolves relative URLs against the source watch URL.
    /// E.g., "/all-vacancies/?show=123" + "https://employment.ku.dk/all-vacancies/"
    /// → "https://employment.ku.dk/all-vacancies/?show=123"
    /// </summary>
    private static string? NormalizeUrl(string? itemUrl, string? sourceWatchUrl)
    {
        if (string.IsNullOrWhiteSpace(itemUrl)) return null;

        // Already absolute
        if (itemUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            itemUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return itemUrl;

        // Resolve relative URL against source watch URL
        if (!string.IsNullOrWhiteSpace(sourceWatchUrl) &&
            Uri.TryCreate(sourceWatchUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, itemUrl, out var resolved))
        {
            return resolved.ToString();
        }

        return itemUrl;
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
    /// Includes URL slug as stabilizer for title-only keys (F9 fix).
    /// </summary>
    private static string BuildIdentityKey(
        ExtractedObject obj,
        IReadOnlyList<string>? schemaIdentityFields,
        TrackingConfig config)
    {
        // Priority 1: Use the object's pre-computed identity key from ObjectDiffService
        if (!string.IsNullOrWhiteSpace(obj.IdentityKey))
            return NormalizeForIdentity(obj.IdentityKey);

        // Priority 2: Use schema-defined identity fields
        if (schemaIdentityFields is { Count: > 0 })
        {
            var parts = schemaIdentityFields
                .Select(f => NormalizeForIdentity(GetFieldValue(obj, f) ?? ""))
                .Where(p => p.Length > 0);
            var key = string.Join("|", parts);
            if (key.Length > 0) return key;
        }

        // Priority 3: Build from config display fields
        var name = NormalizeForIdentity(GetFieldValue(obj, config.DisplayNameField) ?? "");
        var secondary = config.DisplaySecondaryField is not null
            ? NormalizeForIdentity(GetFieldValue(obj, config.DisplaySecondaryField) ?? "")
            : "";

        // For title-only keys, use URL path as a stabilizer to reduce churn
        // from minor title edits (F9: "ODBORNÝ PRACOVNÍK" vs "ODBORNÝ PRACOVNÍK/-CE")
        if (secondary.Length == 0)
        {
            var url = GetFieldValue(obj, config.UrlField);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var slug = ExtractUrlSlug(url);
                if (slug.Length > 0) return $"{name}|{slug}";
            }
        }

        return secondary.Length > 0 ? $"{name}|{secondary}" : name;
    }

    /// <summary>
    /// Normalizes text for identity comparison: lowercase, trim, normalize diacritics for stability.
    /// Doesn't remove diacritics entirely — just normalizes to NFC form for consistent comparison.
    /// </summary>
    private static string NormalizeForIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>
    /// Extracts a stable slug from a URL path for identity key enrichment.
    /// E.g., "/kariera/zdravotni-laborant-ka/" → "zdravotni-laborant-ka"
    /// </summary>
    private static string ExtractUrlSlug(string url)
    {
        try
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.AbsolutePath
                : url;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[^1].ToLowerInvariant() : "";
        }
        catch
        {
            return "";
        }
    }

    private static string? GetFieldValue(ExtractedObject obj, string fieldName)
    {
        return obj.Fields.TryGetValue(fieldName, out var value) ? value : null;
    }

    private static readonly string[] DeadlineDateFormats =
        ["dd-MM-yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d MMM yyyy", "dd.MM.yyyy",
         "MM/dd/yyyy", "yyyy/MM/dd", "d MMMM yyyy"];

    private static DateTime? ParseDeadline(ExtractedObject obj, TrackingConfig config)
    {
        if (config.DeadlineField is null) return null;
        var deadlineStr = GetFieldValue(obj, config.DeadlineField);
        if (string.IsNullOrWhiteSpace(deadlineStr)) return null;

        // Try explicit formats first (culture-independent), then fall back to general parse
        if (DateTime.TryParseExact(deadlineStr.Trim(), DeadlineDateFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;

        // Fallback for ISO and other unambiguous formats
        return DateTime.TryParse(deadlineStr, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : null;
    }
}
