namespace ChangeDetection.Core.Entities;

/// <summary>
/// Domain-agnostic configuration for item tracking and alert behavior.
/// Stored as part of the WatchGroup's AnalysisProfileJson or as a dedicated property.
/// The LLM generates this during watch setup based on the user's intent.
/// </summary>
public class TrackingConfig
{
    /// <summary>
    /// Domain hint for UI rendering and alert labels.
    /// E.g., "job-listing", "product", "paper", "grant", "regulation".
    /// </summary>
    public string ItemType { get; set; } = "item";

    /// <summary>
    /// Label for the tracked items in this domain.
    /// E.g., "position", "listing", "paper", "opportunity".
    /// </summary>
    public string ItemLabel { get; set; } = "item";

    /// <summary>
    /// Label for the alert action verb.
    /// E.g., "APPLY" for jobs, "BUY" for products, "READ" for papers.
    /// </summary>
    public string ActionVerb { get; set; } = "ACT";

    /// <summary>
    /// Which extracted field name maps to DisplayName on TrackedItem.
    /// Default "title" works for jobs, papers, products.
    /// </summary>
    public string DisplayNameField { get; set; } = "title";

    /// <summary>
    /// Which extracted field name maps to DisplaySecondary.
    /// E.g., "company" for jobs, "price" for products, "journal" for papers.
    /// </summary>
    public string? DisplaySecondaryField { get; set; } = "company";

    /// <summary>
    /// Which extracted field name maps to DisplayContext.
    /// E.g., "location" for jobs, "seller" for products, "year" for papers.
    /// </summary>
    public string? DisplayContextField { get; set; } = "location";

    /// <summary>
    /// Which extracted field name contains the URL.
    /// </summary>
    public string UrlField { get; set; } = "url";

    /// <summary>
    /// Which extracted field name contains the deadline/expiry date.
    /// </summary>
    public string? DeadlineField { get; set; } = "deadline";

    /// <summary>
    /// Dimension names that cause a SILENT alert when they FAIL.
    /// Other dimension FAILs are treated as STRETCH (medium priority).
    /// </summary>
    public List<string> HardFailDimensions { get; set; } = ["dealbreakers", "location"];

    /// <summary>
    /// Number of consecutive absences before confirming an item as expired.
    /// Higher values for flaky sources, lower for reliable ones.
    /// </summary>
    public int AbsenceThreshold { get; set; } = 2;

    /// <summary>
    /// Minimum number of tracked items expected from a source.
    /// If all items disappear in one check (current=0, existing > this threshold),
    /// treat as extraction failure instead of mass removal. Prevents broken fetches
    /// from silently expiring all tracked items.
    /// </summary>
    public int MinimumItemThreshold { get; set; } = 3;

    /// <summary>
    /// Creates a default configuration for job watch scenarios.
    /// </summary>
    public static TrackingConfig ForJobs() => new()
    {
        ItemType = "job-listing",
        ItemLabel = "position",
        ActionVerb = "APPLY",
        DisplayNameField = "title",
        DisplaySecondaryField = "company",
        DisplayContextField = "location",
        DeadlineField = "deadline",
        HardFailDimensions = ["dealbreakers", "location"]
    };

    /// <summary>
    /// Creates a default configuration for product watch scenarios.
    /// </summary>
    public static TrackingConfig ForProducts() => new()
    {
        ItemType = "product",
        ItemLabel = "product",
        ActionVerb = "BUY",
        DisplayNameField = "name",
        DisplaySecondaryField = "price",
        DisplayContextField = "seller",
        UrlField = "url",
        DeadlineField = null,
        HardFailDimensions = ["price", "availability"]
    };

    /// <summary>
    /// Creates a default configuration for research/paper watch scenarios.
    /// </summary>
    public static TrackingConfig ForResearch() => new()
    {
        ItemType = "paper",
        ItemLabel = "paper",
        ActionVerb = "READ",
        DisplayNameField = "title",
        DisplaySecondaryField = "journal",
        DisplayContextField = "authors",
        DeadlineField = null,
        HardFailDimensions = ["relevance", "field"]
    };
}
