namespace ChangeDetection.Core.Entities;

/// <summary>
/// Portable catalog format for sharing verified portal configurations between instances.
/// </summary>
public class CatalogExport
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; }
    public List<CatalogPortalEntry> Portals { get; set; } = [];
}

/// <summary>
/// A single portal entry in a catalog export. Contains only portable, non-sensitive data.
/// </summary>
public class CatalogPortalEntry
{
    public required string Url { get; set; }
    public string? Name { get; set; }
    public string? Platform { get; set; }
    public List<string> LocationKeywords { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public int TotalSuccessfulChecks { get; set; }
    public int AverageItemCount { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
}
