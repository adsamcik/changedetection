using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// A saved view representing a filtered/sorted dashboard configuration.
/// </summary>
public class View : IOwnedEntity
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The ID of the user who owns this view.
    /// Guid.Empty represents the default single-user mode owner.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;

    /// <summary>
    /// Display name for the view.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Filter configuration for this view.
    /// </summary>
    public ViewFilters Filters { get; set; } = new();

    /// <summary>
    /// Sort field for this view.
    /// </summary>
    public ViewSortBy SortBy { get; set; } = ViewSortBy.LastChecked;

    /// <summary>
    /// Sort direction.
    /// </summary>
    public bool SortDescending { get; set; } = true;

    /// <summary>
    /// Whether this is a built-in view that cannot be deleted.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Icon to display in navigation (emoji or icon class).
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Display order in navigation.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// When the view was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the view was last modified.
    /// </summary>
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>
/// Filter configuration for a view.
/// </summary>
public class ViewFilters
{
    /// <summary>
    /// Filter by watch status. Null means all statuses.
    /// </summary>
    public WatchStatusFilter? Status { get; set; }

    /// <summary>
    /// Only show watches with recent changes.
    /// </summary>
    public bool ChangedRecently { get; set; }

    /// <summary>
    /// Text search on watch title/URL.
    /// </summary>
    public string? SearchText { get; set; }

    /// <summary>
    /// Only show enabled watches.
    /// </summary>
    public bool? IsEnabled { get; set; }
}

/// <summary>
/// Watch status filter options.
/// </summary>
public enum WatchStatusFilter
{
    All,
    Active,
    Paused,
    Error,
    Checking
}

/// <summary>
/// Sort options for views.
/// </summary>
public enum ViewSortBy
{
    LastChecked,
    Name,
    MostChanges,
    CreatedAt,
    NextCheck
}
