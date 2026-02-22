using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Promotes search results into standalone URL-based watches,
/// linking them to the parent search watch via a shared tag.
/// Supports both manual promotion and auto-promotion via rules.
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

    /// <summary>
    /// Evaluates auto-promotion rules against new search results.
    /// Results matching any enabled rule are promoted to standalone watches.
    /// Already-promoted URLs (existing watches with the same URL) are skipped.
    /// </summary>
    public async Task<IReadOnlyList<WatchedSite>> AutoPromoteAsync(
        Guid parentSearchWatchId,
        IReadOnlyList<SearchResult> newResults,
        CancellationToken ct = default)
    {
        var parentWatch = await watchService.GetByIdAsync(parentSearchWatchId, ct);
        if (parentWatch?.SearchConfig?.AutoPromotionRules is not { Count: > 0 } rules)
            return [];

        var enabledRules = rules.Where(r => r.IsEnabled).ToList();
        if (enabledRules.Count == 0) return [];

        // Get already-promoted URLs to avoid duplicates
        var existingPromoted = await GetPromotedWatchesAsync(parentSearchWatchId, ct);
        var existingUrls = existingPromoted
            .Select(w => w.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var promoted = new List<WatchedSite>();

        foreach (var result in newResults)
        {
            if (existingUrls.Contains(result.Url)) continue;

            var matchingRule = enabledRules.FirstOrDefault(r => MatchesRule(r, result));
            if (matchingRule is null) continue;

            var request = new PromoteSearchResultRequest
            {
                Url = result.Url,
                Name = result.Title,
                CssSelector = matchingRule.CssSelector
            };

            var watch = await PromoteResultAsync(parentSearchWatchId, request, ct);
            if (watch is not null)
            {
                promoted.Add(watch);
                existingUrls.Add(result.Url); // prevent double-promotion in same batch
            }
        }

        if (promoted.Count > 0)
        {
            logger.LogInformation(
                "Auto-promoted {Count} results from search watch {ParentId}",
                promoted.Count, parentSearchWatchId);
        }

        return promoted;
    }

    /// <summary>
    /// Checks if a search result matches an auto-promotion rule.
    /// A rule matches if ALL non-null criteria match.
    /// </summary>
    internal static bool MatchesRule(AutoPromotionRule rule, SearchResult result)
    {
        if (rule.UrlPattern is not null &&
            !MatchGlob(result.Url, rule.UrlPattern))
            return false;

        if (rule.TitleContains is not null &&
            !result.Title.Contains(rule.TitleContains, StringComparison.OrdinalIgnoreCase))
            return false;

        // At least one criterion must be specified
        return rule.UrlPattern is not null || rule.TitleContains is not null;
    }

    /// <summary>
    /// Simple glob matching supporting * (any chars) and ? (single char).
    /// Case-insensitive.
    /// </summary>
    internal static bool MatchGlob(string input, string pattern)
    {
        var regexPattern = "^" +
            System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") +
            "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
