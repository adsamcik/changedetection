using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for category management.
/// </summary>
public static class CategoryEndpoints
{
    public static RouteGroupBuilder MapCategoryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAllCategories)
            .WithName("GetAllCategories")
            .Produces<List<CategoryDto>>();
        
        group.MapGet("/{id}", GetCategoryById)
            .WithName("GetCategoryById")
            .Produces<CategoryDto>()
            .Produces(404);
        
        group.MapPost("/", CreateCategory)
            .WithName("CreateCategory")
            .Produces<CategoryDto>(201);
        
        group.MapPut("/{id}", UpdateCategory)
            .WithName("UpdateCategory")
            .Produces<CategoryDto>()
            .Produces(404);
        
        group.MapDelete("/{id}", DeleteCategory)
            .WithName("DeleteCategory")
            .Produces(204)
            .Produces(400)
            .Produces(404);
        
        group.MapPost("/reorder", ReorderCategories)
            .WithName("ReorderCategories")
            .Produces(204);
        
        return group;
    }
    
    private static async Task<IResult> GetAllCategories(
        ICategoryService categoryService,
        CancellationToken ct)
    {
        var categories = await categoryService.GetAllAsync(ct);
        var watchCounts = await categoryService.GetWatchCountsAsync(ct);
        
        var dtos = categories.Select(c => MapToDto(c, watchCounts.GetValueOrDefault(c.Id))).ToList();
        return Results.Ok(dtos);
    }
    
    private static async Task<IResult> GetCategoryById(
        string id,
        ICategoryService categoryService,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");
        
        var category = await categoryService.GetByIdAsync(guidId, ct);
        if (category == null)
            return Results.NotFound();
        
        var watchCounts = await categoryService.GetWatchCountsAsync(ct);
        return Results.Ok(MapToDto(category, watchCounts.GetValueOrDefault(category.Id)));
    }
    
    private static async Task<IResult> CreateCategory(
        CategoryCreateDto dto,
        ICategoryService categoryService,
        CancellationToken ct)
    {
        var request = new CreateCategoryRequest
        {
            Name = dto.Name,
            Description = dto.Description,
            Color = dto.Color
        };
        
        var category = await categoryService.CreateAsync(request, ct);
        return Results.Created($"/api/categories/{category.Id}", MapToDto(category, 0));
    }
    
    private static async Task<IResult> UpdateCategory(
        string id,
        CategoryUpdateDto dto,
        ICategoryService categoryService,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");
        
        try
        {
            var request = new UpdateCategoryRequest
            {
                Name = dto.Name,
                Description = dto.Description,
                Color = dto.Color
            };
            
            var category = await categoryService.UpdateAsync(guidId, request, ct);
            var watchCounts = await categoryService.GetWatchCountsAsync(ct);
            return Results.Ok(MapToDto(category, watchCounts.GetValueOrDefault(category.Id)));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound();
        }
    }
    
    private static async Task<IResult> DeleteCategory(
        string id,
        ICategoryService categoryService,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");
        
        try
        {
            await categoryService.DeleteAsync(guidId, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Cannot delete"))
        {
            return Results.BadRequest(ex.Message);
        }
    }
    
    private static async Task<IResult> ReorderCategories(
        CategoryReorderDto dto,
        ICategoryService categoryService,
        CancellationToken ct)
    {
        var ids = dto.CategoryIds.Select(id => Guid.Parse(id)).ToList();
        await categoryService.ReorderAsync(ids, ct);
        return Results.NoContent();
    }
    
    private static CategoryDto MapToDto(Category category, int watchCount) => new()
    {
        Id = category.Id.ToString(),
        Name = category.Name,
        Description = category.Description,
        Color = category.Color,
        SortOrder = category.SortOrder,
        IsDefault = category.IsDefault,
        WatchCount = watchCount,
        CreatedAt = category.CreatedAt
    };
}

// DTOs for category endpoints
public class CategoryDto
{
    public string Id { get; set; } = "";
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string Color { get; set; } = "#6B7280";
    public int SortOrder { get; set; }
    public bool IsDefault { get; set; }
    public int WatchCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CategoryCreateDto
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
}

public class CategoryUpdateDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
}

public class CategoryReorderDto
{
    public List<string> CategoryIds { get; set; } = [];
}
