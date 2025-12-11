using ChangeDetection.Core;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services;

/// <summary>
/// Server-side implementation of category service with direct database access.
/// </summary>
public class ServerCategoryService(
    IRepository<Category> categoryRepo,
    IRepository<WatchedSite> watchRepo,
    ILogger<ServerCategoryService> logger) : ICategoryService
{
    private const string DefaultCategoryName = "Uncategorized";
    private const string DefaultCategoryColor = "#6B7280";
    
    public async Task<IEnumerable<Category>> GetAllAsync(CancellationToken ct = default)
    {
        var categories = await categoryRepo.GetAllAsync(ct);
        return categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name);
    }
    
    public async Task<Category?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await categoryRepo.GetByIdAsync(id, ct);
    }
    
    public async Task<Category> GetDefaultCategoryAsync(CancellationToken ct = default)
    {
        var defaultCategory = await categoryRepo.FirstOrDefaultAsync(c => c.IsDefault, ct);
        
        if (defaultCategory != null)
            return defaultCategory;
        
        // Create default category if it doesn't exist
        logger.LogInformation("Creating default 'Uncategorized' category");
        
        defaultCategory = new Category
        {
            Name = DefaultCategoryName,
            Description = "Watches that haven't been assigned to a specific category",
            Color = DefaultCategoryColor,
            SortOrder = int.MaxValue, // Always last
            IsDefault = true
        };
        
        await categoryRepo.InsertAsync(defaultCategory, ct);
        return defaultCategory;
    }
    
    public async Task<Category> CreateAsync(CreateCategoryRequest request, CancellationToken ct = default)
    {
        // Get max sort order
        var categories = await categoryRepo.GetAllAsync(ct);
        var maxSortOrder = categories.Any() 
            ? categories.Where(c => !c.IsDefault).Max(c => c.SortOrder) 
            : 0;
        
        var category = new Category
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Color = request.Color ?? TagColorGenerator.GetPalette()[maxSortOrder % TagColorGenerator.GetPalette().Count],
            SortOrder = maxSortOrder + 1,
            IsDefault = false
        };
        
        await categoryRepo.InsertAsync(category, ct);
        logger.LogInformation("Created category {Id}: {Name}", category.Id, category.Name);
        
        return category;
    }
    
    public async Task<Category> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default)
    {
        var category = await categoryRepo.GetByIdAsync(id, ct) 
            ?? throw new InvalidOperationException($"Category {id} not found");
        
        if (request.Name != null)
            category.Name = request.Name.Trim();
        
        if (request.Description != null)
            category.Description = request.Description.Trim();
        
        if (request.Color != null)
            category.Color = request.Color;
        
        category.UpdatedAt = DateTime.UtcNow;
        
        await categoryRepo.UpdateAsync(category, ct);
        logger.LogInformation("Updated category {Id}: {Name}", category.Id, category.Name);
        
        return category;
    }
    
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var category = await categoryRepo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException($"Category {id} not found");
        
        if (category.IsDefault)
            throw new InvalidOperationException("Cannot delete the default category");
        
        // Get default category to reassign watches
        var defaultCategory = await GetDefaultCategoryAsync(ct);
        
        // Move all watches from this category to uncategorized
        var watches = await watchRepo.FindAsync(w => w.CategoryId == id, ct);
        foreach (var watch in watches)
        {
            watch.CategoryId = defaultCategory.Id;
            watch.UpdatedAt = DateTime.UtcNow;
            await watchRepo.UpdateAsync(watch, ct);
        }
        
        await categoryRepo.DeleteAsync(id, ct);
        logger.LogInformation("Deleted category {Id}: {Name}, moved {Count} watches to Uncategorized", 
            id, category.Name, watches.Count());
    }
    
    public async Task<Dictionary<Guid, int>> GetWatchCountsAsync(CancellationToken ct = default)
    {
        var watches = await watchRepo.GetAllAsync(ct);
        var defaultCategory = await GetDefaultCategoryAsync(ct);
        
        var counts = watches
            .GroupBy(w => w.CategoryId ?? defaultCategory.Id)
            .ToDictionary(g => g.Key, g => g.Count());
        
        return counts;
    }
    
    public async Task ReorderAsync(IEnumerable<Guid> categoryIds, CancellationToken ct = default)
    {
        var order = 0;
        foreach (var id in categoryIds)
        {
            var category = await categoryRepo.GetByIdAsync(id, ct);
            if (category != null && !category.IsDefault)
            {
                category.SortOrder = order++;
                category.UpdatedAt = DateTime.UtcNow;
                await categoryRepo.UpdateAsync(category, ct);
            }
        }
        
        logger.LogDebug("Reordered {Count} categories", order);
    }
}
