namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for reading/updating search provider settings.
/// </summary>
public class SearchSettingsDto
{
    /// <summary>Base URL for SearXNG instance (e.g., "http://localhost:8080").</summary>
    public string? SearxngUrl { get; set; }

    /// <summary>Default search provider ID.</summary>
    public string DefaultProvider { get; set; } = "searxng";

    /// <summary>Default maximum results per search.</summary>
    public int DefaultMaxResults { get; set; } = 20;

    /// <summary>Timeout in seconds for search requests.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Whether a search provider is configured and available.</summary>
    public bool IsAvailable { get; set; }
}
