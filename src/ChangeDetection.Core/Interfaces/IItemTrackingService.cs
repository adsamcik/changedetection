using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Tracks extracted items through their lifecycle states.
/// Handles deduplication within a watch group (same item from multiple sources).
/// Domain-agnostic: behavior driven by TrackingConfig on the WatchGroup.
/// </summary>
public interface IItemTrackingService
{
    /// <summary>
    /// Process new/removed/modified items from a diff result.
    /// Uses TrackingConfig from the watch group for field mapping and identity keys.
    /// </summary>
    Task<ItemTrackingResult> ProcessDiffAsync(
        Guid watchGroupId,
        Guid sourceWatchId,
        Guid ownerId,
        ObjectDiffResult diffResult,
        string? matchDimensionsJson,
        string? recommendation,
        CancellationToken ct);

    /// <summary>
    /// Process diff with a link to the originating ChangeEvent and explicit schema identity fields.
    /// </summary>
    Task<ItemTrackingResult> ProcessDiffAsync(
        Guid watchGroupId,
        Guid sourceWatchId,
        Guid ownerId,
        ObjectDiffResult diffResult,
        string? matchDimensionsJson,
        string? recommendation,
        Guid? changeEventId,
        IReadOnlyList<string>? schemaIdentityFields,
        CancellationToken ct);

    /// <summary>
    /// Get all tracked items for a watch group.
    /// </summary>
    Task<IReadOnlyList<TrackedItem>> GetItemsAsync(Guid watchGroupId, CancellationToken ct);

    /// <summary>
    /// Get tracked items filtered by state.
    /// </summary>
    Task<IReadOnlyList<TrackedItem>> GetItemsByStateAsync(
        Guid watchGroupId, TrackedItemState state, CancellationToken ct);

    /// <summary>
    /// Transition an item to a new state.
    /// </summary>
    Task<bool> TransitionStateAsync(Guid itemId, TrackedItemState newState, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// Mark an item as viewed by the user.
    /// </summary>
    Task MarkAsSeenAsync(Guid itemId, CancellationToken ct);

    /// <summary>
    /// Expire items whose deadline has passed.
    /// Called periodically by the background service.
    /// </summary>
    Task<int> ExpirePassedDeadlinesAsync(Guid watchGroupId, CancellationToken ct);
}

/// <summary>
/// Result of processing a diff for item tracking.
/// </summary>
public class ItemTrackingResult
{
    /// <summary>Newly created tracked items.</summary>
    public List<TrackedItem> NewItems { get; init; } = [];

    /// <summary>Items that were already tracked (dedup hit from another source).</summary>
    public List<TrackedItem> DuplicateItems { get; init; } = [];

    /// <summary>Items marked as potentially expired (removed from source).</summary>
    public List<TrackedItem> PotentiallyExpired { get; init; } = [];

    /// <summary>Items confirmed expired (absent for 2+ consecutive checks).</summary>
    public List<TrackedItem> ConfirmedExpired { get; init; } = [];
}
