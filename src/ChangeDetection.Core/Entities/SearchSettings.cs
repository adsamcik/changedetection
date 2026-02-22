namespace ChangeDetection.Core.Entities;

/// <summary>
/// Configuration for search providers. Bound from appsettings.json "SearchSettings" section.
/// </summary>
public class SearchSettings
{
    /// <summary>Base URL for a SearXNG instance (e.g., "http://localhost:8080").</summary>
    public string? SearxngUrl { get; set; }

    /// <summary>Google Custom Search API key.</summary>
    public string? GoogleCseApiKey { get; set; }

    /// <summary>Google Custom Search Engine ID (cx parameter).</summary>
    public string? GoogleCseEngineId { get; set; }

    /// <summary>Default search provider ID when none specified.</summary>
    public string DefaultProvider { get; set; } = "searxng";

    /// <summary>Default maximum results per search.</summary>
    public int DefaultMaxResults { get; set; } = 20;

    /// <summary>Timeout in seconds for search requests.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
