namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for listing views.
/// </summary>
public class ViewDto
{
    public string Id { get; set; } = "";
    public required string Name { get; set; }
    public ViewFiltersDto Filters { get; set; } = new();
    public string SortBy { get; set; } = "LastChecked";
    public bool SortDescending { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public string? Icon { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// DTO for creating a new view.
/// </summary>
public class ViewCreateDto
{
    public required string Name { get; set; }
    public ViewFiltersDto Filters { get; set; } = new();
    public string SortBy { get; set; } = "LastChecked";
    public bool SortDescending { get; set; } = true;
    public string? Icon { get; set; }
}

/// <summary>
/// DTO for updating a view.
/// </summary>
public class ViewUpdateDto
{
    public string? Name { get; set; }
    public ViewFiltersDto? Filters { get; set; }
    public string? SortBy { get; set; }
    public bool? SortDescending { get; set; }
    public string? Icon { get; set; }
    public int? DisplayOrder { get; set; }
}

/// <summary>
/// Filter configuration DTO.
/// </summary>
public class ViewFiltersDto
{
    /// <summary>
    /// Filter by watch status: All, Active, Paused, Error, Checking.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Only show watches with recent changes.
    /// </summary>
    public bool ChangedRecently { get; set; }

    /// <summary>
    /// Text search on watch title/URL.
    /// </summary>
    public string? SearchText { get; set; }

    /// <summary>
    /// Only show enabled watches. Null means all.
    /// </summary>
    public bool? IsEnabled { get; set; }
}
