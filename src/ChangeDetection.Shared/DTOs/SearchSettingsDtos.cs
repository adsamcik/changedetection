namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for reading/updating search provider settings.
/// </summary>
public class SearchSettingsDto
{
    /// <summary>Base URL for SearXNG instance (e.g., "http://localhost:8080").</summary>
    public string? SearxngUrl { get; set; }

    /// <summary>Google Custom Search API key.</summary>
    public string? GoogleCseApiKey { get; set; }

    /// <summary>Google Custom Search Engine ID (cx parameter).</summary>
    public string? GoogleCseEngineId { get; set; }

    /// <summary>Brave Search API subscription token.</summary>
    public string? BraveApiKey { get; set; }

    /// <summary>Default search provider ID.</summary>
    public string DefaultProvider { get; set; } = "searxng";

    /// <summary>Default maximum results per search.</summary>
    public int DefaultMaxResults { get; set; } = 20;

    /// <summary>Timeout in seconds for search requests.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Whether a search provider is configured and available.</summary>
    public bool IsAvailable { get; set; }
}

/// <summary>
/// Request DTO to promote a search result into a standalone watch.
/// </summary>
public class PromoteSearchResultDto
{
    /// <summary>URL from the search result to monitor.</summary>
    public required string Url { get; set; }

    /// <summary>Optional name for the new watch. Defaults to search result title if available.</summary>
    public string? Name { get; set; }

    /// <summary>Optional CSS selector to focus monitoring on specific content.</summary>
    public string? CssSelector { get; set; }

    /// <summary>Optional check interval in minutes. Defaults to parent watch's interval.</summary>
    public int? CheckIntervalMinutes { get; set; }
}
