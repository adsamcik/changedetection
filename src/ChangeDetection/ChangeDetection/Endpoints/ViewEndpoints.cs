using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for managing views.
/// </summary>
public static class ViewEndpoints
{
    /// <summary>
    /// Built-in views that are seeded on first access.
    /// </summary>
    private static readonly List<View> BuiltInViews =
    [
        new View
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Name = "Errors",
            Icon = "⚠️",
            IsBuiltIn = true,
            DisplayOrder = 1,
            Filters = new ViewFilters { Status = WatchStatusFilter.Error },
            SortBy = ViewSortBy.LastChecked,
            SortDescending = true
        },
        new View
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Name = "Recently Changed",
            Icon = "🔔",
            IsBuiltIn = true,
            DisplayOrder = 2,
            Filters = new ViewFilters { ChangedRecently = true },
            SortBy = ViewSortBy.LastChecked,
            SortDescending = true
        }
    ];

    public static RouteGroupBuilder MapViewEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAllViews)
            .WithName("GetAllViews")
            .Produces<List<ViewDto>>();

        group.MapGet("/{id}", GetViewById)
            .WithName("GetViewById")
            .Produces<ViewDto>()
            .Produces(404);

        group.MapPost("/", CreateView)
            .WithName("CreateView")
            .Produces<ViewDto>(201);

        group.MapPut("/{id}", UpdateView)
            .WithName("UpdateView")
            .Produces<ViewDto>()
            .Produces(404)
            .Produces(400);

        group.MapDelete("/{id}", DeleteView)
            .WithName("DeleteView")
            .Produces(204)
            .Produces(404)
            .Produces(400);

        return group;
    }

    private static async Task<IResult> GetAllViews(
        IRepository<View> viewRepo,
        CancellationToken ct)
    {
        await EnsureBuiltInViewsExist(viewRepo, ct);

        var views = await viewRepo.GetAllAsync(ct);
        var dtos = views
            .OrderBy(v => v.IsBuiltIn ? 0 : 1)
            .ThenBy(v => v.DisplayOrder)
            .ThenBy(v => v.Name)
            .Select(ToDto)
            .ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetViewById(
        string id,
        IRepository<View> viewRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        await EnsureBuiltInViewsExist(viewRepo, ct);

        var view = await viewRepo.GetByIdAsync(guidId, ct);
        if (view == null)
            return Results.NotFound();

        return Results.Ok(ToDto(view));
    }

    private static async Task<IResult> CreateView(
        ViewCreateDto dto,
        IRepository<View> viewRepo,
        CancellationToken ct)
    {
        var existingViews = await viewRepo.GetAllAsync(ct);
        var maxOrder = existingViews.Any() ? existingViews.Max(v => v.DisplayOrder) : 0;

        var view = new View
        {
            Name = dto.Name,
            Icon = dto.Icon,
            IsBuiltIn = false,
            DisplayOrder = maxOrder + 1,
            Filters = ToFilters(dto.Filters),
            SortBy = ParseSortBy(dto.SortBy),
            SortDescending = dto.SortDescending
        };

        await viewRepo.InsertAsync(view, ct);
        return Results.Created($"/api/views/{view.Id}", ToDto(view));
    }

    private static async Task<IResult> UpdateView(
        string id,
        ViewUpdateDto dto,
        IRepository<View> viewRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var view = await viewRepo.GetByIdAsync(guidId, ct);
        if (view == null)
            return Results.NotFound();

        if (view.IsBuiltIn)
            return Results.BadRequest("Cannot modify built-in views");

        if (dto.Name != null)
            view.Name = dto.Name;
        if (dto.Icon != null)
            view.Icon = dto.Icon;
        if (dto.Filters != null)
            view.Filters = ToFilters(dto.Filters);
        if (dto.SortBy != null)
            view.SortBy = ParseSortBy(dto.SortBy);
        if (dto.SortDescending.HasValue)
            view.SortDescending = dto.SortDescending.Value;
        if (dto.DisplayOrder.HasValue)
            view.DisplayOrder = dto.DisplayOrder.Value;

        view.ModifiedAt = DateTime.UtcNow;

        await viewRepo.UpdateAsync(view, ct);
        return Results.Ok(ToDto(view));
    }

    private static async Task<IResult> DeleteView(
        string id,
        IRepository<View> viewRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var view = await viewRepo.GetByIdAsync(guidId, ct);
        if (view == null)
            return Results.NotFound();

        if (view.IsBuiltIn)
            return Results.BadRequest("Cannot delete built-in views");

        await viewRepo.DeleteAsync(guidId, ct);
        return Results.NoContent();
    }

    private static async Task EnsureBuiltInViewsExist(IRepository<View> viewRepo, CancellationToken ct)
    {
        foreach (var builtIn in BuiltInViews)
        {
            var existing = await viewRepo.GetByIdAsync(builtIn.Id, ct);
            if (existing == null)
            {
                await viewRepo.InsertAsync(builtIn, ct);
            }
        }
    }

    private static ViewDto ToDto(View view) => new()
    {
        Id = view.Id.ToString(),
        Name = view.Name,
        Icon = view.Icon,
        IsBuiltIn = view.IsBuiltIn,
        DisplayOrder = view.DisplayOrder,
        Filters = new ViewFiltersDto
        {
            Status = view.Filters.Status?.ToString(),
            ChangedRecently = view.Filters.ChangedRecently,
            SearchText = view.Filters.SearchText,
            IsEnabled = view.Filters.IsEnabled
        },
        SortBy = view.SortBy.ToString(),
        SortDescending = view.SortDescending,
        CreatedAt = view.CreatedAt
    };

    private static ViewFilters ToFilters(ViewFiltersDto dto) => new()
    {
        Status = string.IsNullOrEmpty(dto.Status) ? null : Enum.Parse<WatchStatusFilter>(dto.Status, ignoreCase: true),
        ChangedRecently = dto.ChangedRecently,
        SearchText = dto.SearchText,
        IsEnabled = dto.IsEnabled
    };

    private static ViewSortBy ParseSortBy(string sortBy)
    {
        if (Enum.TryParse<ViewSortBy>(sortBy, ignoreCase: true, out var result))
            return result;
        return ViewSortBy.LastChecked;
    }
}
