using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.JobWatch;

/// <summary>
/// Tracks job listings through their lifecycle and handles cross-portal deduplication.
/// Uses identity keys from the extraction schema to match listings across sources.
/// </summary>
public class ListingTrackingService(
    IRepository<TrackedListing> listingRepo,
    IAlertPolicyService alertPolicy,
    ILogger<ListingTrackingService> logger) : IListingTrackingService
{
    /// <summary>
    /// Required number of consecutive absences before confirming a listing as expired.
    /// </summary>
    private const int AbsenceThreshold = 2;

    public async Task<ListingTrackingResult> ProcessDiffAsync(
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

    public async Task<ListingTrackingResult> ProcessDiffAsync(
        Guid watchGroupId,
        Guid sourceWatchId,
        Guid ownerId,
        ObjectDiffResult diffResult,
        string? matchDimensionsJson,
        string? recommendation,
        Guid? changeEventId,
        CancellationToken ct)
    {
        var result = new ListingTrackingResult();
        var existing = await GetListingsAsync(watchGroupId, ct);
        var existingByKey = existing.ToDictionary(l => l.IdentityKey, StringComparer.OrdinalIgnoreCase);

        // Process added items
        foreach (var added in diffResult.AddedItems)
        {
            var identityKey = BuildIdentityKey(added);
            if (string.IsNullOrWhiteSpace(identityKey)) continue;

            if (existingByKey.TryGetValue(identityKey, out var existingListing))
            {
                // Cross-portal dedup — same listing found from another watch
                if (existingListing.SourceWatchId != sourceWatchId &&
                    !existingListing.AdditionalSourceWatchIds.Contains(sourceWatchId))
                {
                    existingListing.AdditionalSourceWatchIds.Add(sourceWatchId);
                    await listingRepo.UpdateAsync(existingListing, ct);
                }

                // Reset absence counter if listing reappeared
                if (existingListing.ConsecutiveAbsences > 0)
                {
                    existingListing.ConsecutiveAbsences = 0;
                    await listingRepo.UpdateAsync(existingListing, ct);
                }

                result.DuplicateListings.Add(existingListing);
                continue;
            }

            var deadline = ParseDeadline(added);
            var policyResult = alertPolicy.Evaluate(matchDimensionsJson, recommendation, deadline);

            var listing = new TrackedListing
            {
                OwnerId = ownerId,
                WatchGroupId = watchGroupId,
                SourceWatchId = sourceWatchId,
                IdentityKey = identityKey,
                Title = GetFieldValue(added, "title"),
                Company = GetFieldValue(added, "company"),
                Location = GetFieldValue(added, "location"),
                Url = GetFieldValue(added, "url"),
                Deadline = deadline,
                State = ListingState.New,
                AlertLevel = policyResult.AlertLevel,
                MatchDimensionsJson = matchDimensionsJson,
                Recommendation = recommendation,
                OriginChangeEventId = changeEventId,
                ExtractedDataJson = JsonSerializer.Serialize(added.Fields)
            };

            await listingRepo.InsertAsync(listing, ct);
            result.NewListings.Add(listing);

            logger.LogDebug(
                "New listing tracked: {Title} at {Company} — {AlertLevel}",
                listing.Title, listing.Company, listing.AlertLevel);
        }

        // Process removed items
        foreach (var removed in diffResult.RemovedItems)
        {
            var identityKey = BuildIdentityKey(removed);
            if (string.IsNullOrWhiteSpace(identityKey)) continue;

            if (!existingByKey.TryGetValue(identityKey, out var trackedListing)) continue;
            if (trackedListing.State is ListingState.Expired or ListingState.Dismissed) continue;

            trackedListing.ConsecutiveAbsences++;

            if (trackedListing.ConsecutiveAbsences >= AbsenceThreshold)
            {
                trackedListing.State = ListingState.Expired;
                trackedListing.ExpiryReason = Core.Entities.ExpiryReason.RemovedFromSource;
                trackedListing.StateChangedAt = DateTime.UtcNow;
                result.ConfirmedExpired.Add(trackedListing);
                logger.LogInformation("Listing confirmed expired (removed from source): {Title} at {Company}",
                    trackedListing.Title, trackedListing.Company);
            }
            else
            {
                result.PotentiallyExpired.Add(trackedListing);
                logger.LogDebug("Listing absent ({Count}x): {Title}", trackedListing.ConsecutiveAbsences, trackedListing.Title);
            }

            await listingRepo.UpdateAsync(trackedListing, ct);
        }

        return result;
    }

    public async Task<IReadOnlyList<TrackedListing>> GetListingsAsync(Guid watchGroupId, CancellationToken ct)
    {
        var results = await listingRepo.FindAsync(l => l.WatchGroupId == watchGroupId, ct);
        return results.ToList();
    }

    public async Task<IReadOnlyList<TrackedListing>> GetListingsByStateAsync(
        Guid watchGroupId, ListingState state, CancellationToken ct)
    {
        var results = await listingRepo.FindAsync(
            l => l.WatchGroupId == watchGroupId && l.State == state, ct);
        return results.ToList();
    }

    public async Task<bool> TransitionStateAsync(Guid listingId, ListingState newState, string? reason, CancellationToken ct)
    {
        var listing = await listingRepo.GetByIdAsync(listingId, ct);
        if (listing is null) return false;

        if (!IsValidTransition(listing.State, newState))
        {
            logger.LogWarning("Invalid state transition {From}→{To} for listing {Id}", listing.State, newState, listingId);
            return false;
        }

        listing.State = newState;
        listing.StateChangedAt = DateTime.UtcNow;

        switch (newState)
        {
            case ListingState.Alerted:
                listing.AlertedAt = DateTime.UtcNow;
                break;
            case ListingState.Seen:
                listing.SeenAt = DateTime.UtcNow;
                break;
            case ListingState.Dismissed:
                listing.DismissalReason = reason;
                break;
            case ListingState.Expired:
                listing.ExpiryReason ??= Core.Entities.ExpiryReason.Manual;
                break;
        }

        await listingRepo.UpdateAsync(listing, ct);
        return true;
    }

    public async Task MarkAsSeenAsync(Guid listingId, CancellationToken ct)
    {
        var listing = await listingRepo.GetByIdAsync(listingId, ct);
        if (listing is null) return;

        if (listing.State is ListingState.New or ListingState.Alerted)
        {
            listing.State = ListingState.Seen;
            listing.SeenAt = DateTime.UtcNow;
            listing.StateChangedAt = DateTime.UtcNow;
            await listingRepo.UpdateAsync(listing, ct);
        }
    }

    public async Task<int> ExpirePassedDeadlinesAsync(Guid watchGroupId, CancellationToken ct)
    {
        var active = await listingRepo.FindAsync(
            l => l.WatchGroupId == watchGroupId
                 && l.State != ListingState.Expired
                 && l.State != ListingState.Dismissed, ct);

        var expiredCount = 0;
        var now = DateTime.UtcNow.Date;

        foreach (var listing in active)
        {
            if (listing.Deadline is null || listing.Deadline.Value.Date >= now) continue;

            listing.State = ListingState.Expired;
            listing.ExpiryReason = Core.Entities.ExpiryReason.DeadlinePassed;
            listing.StateChangedAt = DateTime.UtcNow;
            await listingRepo.UpdateAsync(listing, ct);
            expiredCount++;
        }

        if (expiredCount > 0)
            logger.LogInformation("Expired {Count} listings with passed deadlines in group {GroupId}", expiredCount, watchGroupId);

        return expiredCount;
    }

    private static bool IsValidTransition(ListingState from, ListingState to) => (from, to) switch
    {
        (ListingState.New, ListingState.Alerted) => true,
        (ListingState.New, ListingState.Seen) => true,
        (ListingState.New, ListingState.Dismissed) => true,
        (ListingState.New, ListingState.Expired) => true,
        (ListingState.Alerted, ListingState.Seen) => true,
        (ListingState.Alerted, ListingState.Applied) => true,
        (ListingState.Alerted, ListingState.Dismissed) => true,
        (ListingState.Alerted, ListingState.Expired) => true,
        (ListingState.Seen, ListingState.Applied) => true,
        (ListingState.Seen, ListingState.Dismissed) => true,
        (ListingState.Seen, ListingState.Expired) => true,
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
