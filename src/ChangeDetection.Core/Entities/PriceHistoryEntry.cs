namespace ChangeDetection.Core.Entities;

/// <summary>
/// Historical record of a price/value extraction for time-series tracking.
/// Stored in a separate LiteDB collection for efficient time-range queries.
/// </summary>
public class PriceHistoryEntry
{
    /// <summary>
    /// Unique identifier for this history entry.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The watch this price belongs to.
    /// </summary>
    public required Guid WatchId { get; set; }

    /// <summary>
    /// Optional reference to the specific schema field this value came from.
    /// Null for single-value watches without schema.
    /// </summary>
    public Guid? SchemaFieldId { get; set; }

    /// <summary>
    /// The field name for display/querying (e.g., "Price", "Stock Count").
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// The extracted numeric value.
    /// </summary>
    public required decimal Value { get; set; }

    /// <summary>
    /// Currency code (e.g., "CZK", "EUR", "USD").
    /// Null for non-currency numeric fields.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Normalized stock status at this point in time.
    /// </summary>
    public StockStatus? StockStatus { get; set; }

    /// <summary>
    /// The raw price text as extracted from the page.
    /// Preserved for debugging and display (e.g., "2 499 Kč").
    /// </summary>
    public string? RawPriceText { get; set; }

    /// <summary>
    /// The raw stock status text as extracted from the page.
    /// Preserved for debugging (e.g., "Skladem > 5 ks", "UKONČENO").
    /// </summary>
    public string? RawStockText { get; set; }

    /// <summary>
    /// When this value was extracted.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Reference to the snapshot that contained this extraction.
    /// </summary>
    public Guid? SnapshotId { get; set; }

    /// <summary>
    /// Optional object identity key for multi-item watches.
    /// Allows tracking price history per-item in a product listing.
    /// </summary>
    public string? ObjectIdentityKey { get; set; }
}
