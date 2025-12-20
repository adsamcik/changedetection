using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a category for organizing watched sites.
/// Categories are user-defined and limited in number (~10).
/// The full list is passed to the LLM for classification.
/// </summary>
public class Category : IOwnedEntity
{
    /// <summary>
    /// Unique identifier for the category.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The ID of the user who owns this category.
    /// Guid.Empty represents the default single-user mode owner.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;
    
    /// <summary>
    /// Display name for the category.
    /// </summary>
    public required string Name { get; set; }
    
    /// <summary>
    /// Optional description to help LLM understand the category's purpose.
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// Hex color code for UI display (e.g., "#3B82F6").
    /// </summary>
    public string Color { get; set; } = "#6B7280";
    
    /// <summary>
    /// Sort order for display in UI.
    /// </summary>
    public int SortOrder { get; set; }
    
    /// <summary>
    /// Whether this is the default "Uncategorized" category.
    /// Only one category should have this set to true.
    /// </summary>
    public bool IsDefault { get; set; }
    
    /// <summary>
    /// When this category was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When this category was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
