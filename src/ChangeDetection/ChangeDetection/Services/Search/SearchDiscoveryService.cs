using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Promotes search results into standalone URL-based watches,
/// linking them to the parent search watch via a shared tag.
/// </summary>
public class SearchDiscoveryService(
    IWatchService watchService,
    ILogger<SearchDiscoveryService> logger) : ISearchDiscoveryService
{
    internal const string PromotedFromTagPrefix = "promoted-from:";

    public async Task<WatchedSite?> PromoteResultAsync(
        Guid parentSearchWatchId,
        PromoteSearchResultRequest request,
        CancellationToken ct = default)
    {
        var parentWatch = await watchService.GetByIdAsync(parentSearchWatchId, ct);
        if (parentWatch is null)
        {
            logger.LogWarning("Cannot promote: parent search watch {Id} not found", parentSearchWatchId);
            return null;
        }

        if (parentWatch.SourceType != SourceType.Search)
        {
            logger.LogWarning("Cannot promote from non-search watch {Id}", parentSearchWatchId);
            return null;
        }

        // Build the tag that links promoted watches to their parent search
        var promotionTag = $"{PromotedFromTagPrefix}{parentSearchWatchId}";

        var createRequest = new CreateWatchRequest
        {
            Url = request.Url,
            Name = request.Name,
            CssSelector = request.CssSelector,
            CheckInterval = request.CheckInterval ?? parentWatch.CheckInterval,
            Tags = [promotionTag],
            Notifications = parentWatch.Notifications,
            FetchSettings = parentWatch.FetchSettings
        };

        var watch = await watchService.CreateWatchAsync(createRequest, ct);

        logger.LogInformation(
            "Promoted search result {Url} from search watch {ParentId} → new watch {WatchId}",
            request.Url, parentSearchWatchId, watch.Id);

        return watch;
    }

    public async Task<IReadOnlyList<WatchedSite>> GetPromotedWatchesAsync(
        Guid parentSearchWatchId,
        CancellationToken ct = default)
    {
        var promotionTag = $"{PromotedFromTagPrefix}{parentSearchWatchId}";
        var allWatches = await watchService.GetAllAsync(ct);

        return allWatches
            .Where(w => w.Tags.Contains(promotionTag))
            .ToList();
    }
}
