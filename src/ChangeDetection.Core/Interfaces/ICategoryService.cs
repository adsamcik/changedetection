using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for managing watch categories.
/// Categories are user-defined and limited in number.
/// </summary>
public interface ICategoryService
{
    /// <summary>
    /// Gets all categories ordered by SortOrder.
    /// </summary>
    Task<IEnumerable<Category>> GetAllAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets a category by ID.
    /// </summary>
    Task<Category?> GetByIdAsync(Guid id, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the default "Uncategorized" category.
    /// Creates it if it doesn't exist.
    /// </summary>
    Task<Category> GetDefaultCategoryAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Creates a new category.
    /// </summary>
    Task<Category> CreateAsync(CreateCategoryRequest request, CancellationToken ct = default);
    
    /// <summary>
    /// Updates an existing category.
    /// </summary>
    Task<Category> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default);
    
    /// <summary>
    /// Deletes a category. Watches in this category are moved to Uncategorized.
    /// Cannot delete the default category.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the count of watches in each category.
    /// </summary>
    Task<Dictionary<Guid, int>> GetWatchCountsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Reorders categories by setting their SortOrder.
    /// </summary>
    Task ReorderAsync(IEnumerable<Guid> categoryIds, CancellationToken ct = default);
}

/// <summary>
/// Request to create a new category.
/// </summary>
public class CreateCategoryRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
}

/// <summary>
/// Request to update a category.
/// </summary>
public class UpdateCategoryRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
}
