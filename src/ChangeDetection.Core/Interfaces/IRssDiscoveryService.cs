namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Discovers RSS/Atom feed links from HTML content.
/// Used to offer feed-based monitoring as an alternative to HTML scraping.
/// </summary>
public interface IRssDiscoveryService
{
    /// <summary>
    /// Scans a URL's HTML for RSS/Atom feed links.
    /// </summary>
    Task<RssDiscoveryResult> DiscoverFeedsAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Result of RSS/Atom feed discovery on a page.
/// </summary>
public record RssDiscoveryResult
{
    /// <summary>The URL that was scanned.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>Discovered feed links.</summary>
    public required IReadOnlyList<DiscoveredFeed> Feeds { get; init; }

    /// <summary>Whether any feeds were found.</summary>
    public bool HasFeeds => Feeds.Count > 0;
}

/// <summary>
/// A discovered RSS/Atom feed from a web page.
/// </summary>
public record DiscoveredFeed
{
    /// <summary>URL of the feed.</summary>
    public required string FeedUrl { get; init; }

    /// <summary>Title of the feed (from title attribute).</summary>
    public string? Title { get; init; }

    /// <summary>MIME type (e.g., "application/rss+xml", "application/atom+xml").</summary>
    public required string MimeType { get; init; }

    /// <summary>Whether this is an Atom feed (vs RSS).</summary>
    public bool IsAtom => MimeType.Contains("atom", StringComparison.OrdinalIgnoreCase);
}
