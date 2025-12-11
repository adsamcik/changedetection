namespace ChangeDetection.Core.Entities;

/// <summary>
/// Defines the schema for extracting structured objects from a webpage.
/// Used for list-type content like events, products, articles, etc.
/// </summary>
public class ExtractionSchema
{
    /// <summary>
    /// CSS or XPath selector that identifies the repeating item container.
    /// Each match represents one object to extract.
    /// </summary>
    public required string ItemSelector { get; set; }

    /// <summary>
    /// Fields to extract from each item.
    /// </summary>
    public List<SchemaField> Fields { get; set; } = [];

    /// <summary>
    /// Names of fields that together uniquely identify an object.
    /// Used for diff matching to detect added/removed/modified items.
    /// LLM-inferred during discovery, user-overridable.
    /// </summary>
    public List<string> IdentityFieldNames { get; set; } = [];

    /// <summary>
    /// Schema version, incremented when schema is modified.
    /// Used to detect schema drift.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// When this schema was discovered/created.
    /// </summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Settings for how object diffs are computed and reported.
    /// </summary>
    public ObjectDiffSettings DiffSettings { get; set; } = new();
}

/// <summary>
/// Defines a single field within an extraction schema.
/// </summary>
public class SchemaField
{
    /// <summary>
    /// Human-readable name for this field (e.g., "Event Title", "Price").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The data type of this field.
    /// </summary>
    public FieldType Type { get; set; } = FieldType.String;

    /// <summary>
    /// CSS or XPath selector relative to the item container.
    /// </summary>
    public required string Selector { get; set; }

    /// <summary>
    /// Whether this field is required for a valid extraction.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Whether this field is part of the object's identity for diff matching.
    /// </summary>
    public bool IsIdentityField { get; set; }

    /// <summary>
    /// Sample value extracted during schema discovery (for preview).
    /// </summary>
    public string? SampleValue { get; set; }

    /// <summary>
    /// Confidence score from LLM during discovery (0-1).
    /// </summary>
    public float? Confidence { get; set; }
}

/// <summary>
/// Data types for schema fields.
/// Fixed vocabulary for LLM inference consistency.
/// </summary>
public enum FieldType
{
    /// <summary>Plain text content.</summary>
    String,

    /// <summary>Date or datetime value.</summary>
    Date,

    /// <summary>URL or link.</summary>
    Url,

    /// <summary>Numeric value (integer or decimal).</summary>
    Number,

    /// <summary>Image URL or source.</summary>
    Image,

    /// <summary>Raw HTML content (preserves markup).</summary>
    Html
}

/// <summary>
/// Settings for object-level diff computation.
/// </summary>
public class ObjectDiffSettings
{
    /// <summary>
    /// Level of detail for diff detection.
    /// </summary>
    public DiffGranularity Granularity { get; set; } = DiffGranularity.Both;

    /// <summary>
    /// Whether to use LLM to score importance of each change.
    /// </summary>
    public bool EnableImportanceScoring { get; set; } = true;

    /// <summary>
    /// Default importance level when LLM scoring is disabled.
    /// </summary>
    public ChangeImportance DefaultImportance { get; set; } = ChangeImportance.Medium;
}

/// <summary>
/// Granularity of object diff detection.
/// </summary>
public enum DiffGranularity
{
    /// <summary>Only detect added/removed items, ignore field changes.</summary>
    ItemsOnly,

    /// <summary>Only detect field-level changes within existing items.</summary>
    FieldLevel,

    /// <summary>Detect both item-level and field-level changes.</summary>
    Both
}

/// <summary>
/// Result of extracting objects from HTML using a schema.
/// </summary>
public class ObjectExtractionResult
{
    /// <summary>
    /// Successfully extracted objects. Null if extraction failed.
    /// </summary>
    public List<ExtractedObject>? Objects { get; set; }

    /// <summary>
    /// Whether extraction completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if extraction failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Whether the page structure no longer matches the schema.
    /// </summary>
    public bool DriftDetected { get; set; }

    /// <summary>
    /// Warnings about objects with duplicate identity values.
    /// </summary>
    public List<string> AmbiguousIdentityWarnings { get; set; } = [];
}

/// <summary>
/// A single object extracted from a webpage.
/// </summary>
public class ExtractedObject
{
    /// <summary>
    /// Field values keyed by field name.
    /// </summary>
    public Dictionary<string, string?> Fields { get; set; } = [];

    /// <summary>
    /// Computed identity string from identity fields for matching.
    /// </summary>
    public string? IdentityKey { get; set; }

    /// <summary>
    /// Index of this object in the extraction order (0-based).
    /// </summary>
    public int Index { get; set; }
}

/// <summary>
/// Result of comparing two sets of extracted objects.
/// </summary>
public class ObjectDiffResult
{
    /// <summary>
    /// Objects that appear in current but not previous.
    /// </summary>
    public List<ExtractedObject> AddedItems { get; set; } = [];

    /// <summary>
    /// Objects that appear in previous but not current.
    /// </summary>
    public List<ExtractedObject> RemovedItems { get; set; } = [];

    /// <summary>
    /// Objects that exist in both but have field changes.
    /// </summary>
    public List<ObjectModification> ModifiedItems { get; set; } = [];

    /// <summary>
    /// Whether any objects had ambiguous identity matches.
    /// </summary>
    public bool HasAmbiguousIdentities { get; set; }

    /// <summary>
    /// Details about ambiguous identity conflicts.
    /// </summary>
    public List<string> AmbiguityDetails { get; set; } = [];

    /// <summary>
    /// Whether any changes were detected.
    /// </summary>
    public bool HasChanges => AddedItems.Count > 0 || RemovedItems.Count > 0 || ModifiedItems.Count > 0;
}

/// <summary>
/// Describes changes to a single object's fields.
/// </summary>
public class ObjectModification
{
    /// <summary>
    /// The identity key of the modified object.
    /// </summary>
    public required string IdentityKey { get; set; }

    /// <summary>
    /// The object before changes.
    /// </summary>
    public required ExtractedObject PreviousObject { get; set; }

    /// <summary>
    /// The object after changes.
    /// </summary>
    public required ExtractedObject CurrentObject { get; set; }

    /// <summary>
    /// Individual field changes.
    /// </summary>
    public List<FieldChange> FieldChanges { get; set; } = [];
}

/// <summary>
/// Describes a change to a single field value.
/// </summary>
public class FieldChange
{
    /// <summary>
    /// Name of the changed field.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Previous value (null if field was added).
    /// </summary>
    public string? OldValue { get; set; }

    /// <summary>
    /// Current value (null if field was removed).
    /// </summary>
    public string? NewValue { get; set; }

    /// <summary>
    /// LLM-scored importance of this specific change.
    /// </summary>
    public ChangeImportance? LlmImportance { get; set; }

    /// <summary>
    /// LLM explanation of why this change matters.
    /// </summary>
    public string? ImportanceReason { get; set; }
}
