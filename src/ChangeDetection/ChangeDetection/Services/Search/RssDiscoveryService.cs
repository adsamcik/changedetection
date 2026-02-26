using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Discovers RSS/Atom feeds by parsing HTML link elements.
/// Uses IContentFetcher to retrieve page HTML and scans for alternate feed links.
/// </summary>
public partial class RssDiscoveryService(
    IContentFetcher contentFetcher,
    ILogger<RssDiscoveryService> logger) : IRssDiscoveryService
{
    private static readonly string[] FeedMimeTypes =
    [
        "application/rss+xml",
        "application/atom+xml",
        "application/xml",
        "text/xml"
    ];

    public async Task<RssDiscoveryResult> DiscoverFeedsAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var fetchResult = await contentFetcher.FetchAsync(url, new FetchOptions(), ct);
            if (!fetchResult.IsSuccess || string.IsNullOrWhiteSpace(fetchResult.Html))
            {
                logger.LogDebug("Failed to fetch {Url} for RSS discovery: {Status}", url, fetchResult.HttpStatusCode);
                return new RssDiscoveryResult { SourceUrl = url, Feeds = [] };
            }

            var feeds = ParseFeedLinks(fetchResult.Html, url);
            logger.LogDebug("Discovered {Count} feeds on {Url}", feeds.Count, url);
            return new RssDiscoveryResult { SourceUrl = url, Feeds = feeds };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "RSS discovery failed for {Url}", url);
            return new RssDiscoveryResult { SourceUrl = url, Feeds = [] };
        }
    }

    /// <summary>
    /// Parses HTML for link[rel=alternate] elements that point to RSS/Atom feeds.
    /// </summary>
    internal static IReadOnlyList<DiscoveredFeed> ParseFeedLinks(string html, string baseUrl)
    {
        var feeds = new List<DiscoveredFeed>();
        var matches = FeedLinkPattern().Matches(html);

        foreach (Match match in matches)
        {
            var attributes = match.Value;

            // Must have rel="alternate"
            var relMatch = RelPattern().Match(attributes);
            if (!relMatch.Success || !relMatch.Groups[1].Value.Contains("alternate", StringComparison.OrdinalIgnoreCase))
                continue;

            // Must have a feed MIME type
            var typeMatch = TypePattern().Match(attributes);
            if (!typeMatch.Success)
                continue;

            var mimeType = typeMatch.Groups[1].Value.Trim();
            if (!FeedMimeTypes.Any(t => mimeType.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Extract href
            var hrefMatch = HrefPattern().Match(attributes);
            if (!hrefMatch.Success)
                continue;

            var href = hrefMatch.Groups[1].Value.Trim();
            var feedUrl = ResolveUrl(href, baseUrl);
            if (feedUrl is null) continue;

            // Extract optional title
            var titleMatch = TitlePattern().Match(attributes);
            var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : null;

            feeds.Add(new DiscoveredFeed
            {
                FeedUrl = feedUrl,
                Title = title,
                MimeType = mimeType
            });
        }

        return feeds;
    }

    internal static string? ResolveUrl(string href, string baseUrl)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, href, out var resolved))
            return resolved.ToString();

        return null;
    }

    [GeneratedRegex(@"<link\b[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FeedLinkPattern();

    [GeneratedRegex(@"rel\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex RelPattern();

    [GeneratedRegex(@"type\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();

    [GeneratedRegex(@"title\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePattern();
}
