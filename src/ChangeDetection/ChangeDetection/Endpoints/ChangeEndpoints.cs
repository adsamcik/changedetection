using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for change history.
/// </summary>
public static class ChangeEndpoints
{
    public static RouteGroupBuilder MapChangeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAllChanges)
            .WithName("GetAllChanges")
            .Produces<List<ChangeListItemDto>>();

        group.MapGet("/{id}", GetChangeById)
            .WithName("GetChangeById")
            .Produces<ChangeDetailDto>()
            .Produces(404);

        group.MapPost("/{id}/viewed", MarkAsViewed)
            .WithName("MarkChangeAsViewed")
            .Produces(204)
            .Produces(404);

        group.MapGet("/unviewed/count", GetUnviewedCount)
            .WithName("GetUnviewedCount")
            .Produces<int>();

        return group;
    }

    private static async Task<IResult> GetAllChanges(
        IRepository<ChangeEvent> eventRepo,
        IRepository<WatchedSite> watchRepo,
        string? watchId,
        int? limit,
        CancellationToken ct)
    {
        IEnumerable<ChangeEvent> events;
        
        if (!string.IsNullOrEmpty(watchId) && Guid.TryParse(watchId, out var guidWatchId))
        {
            events = await eventRepo.FindAsync(e => e.WatchedSiteId == guidWatchId, ct);
        }
        else
        {
            events = await eventRepo.GetAllAsync(ct);
        }
        
        IEnumerable<ChangeEvent> orderedEvents = events.OrderByDescending(e => e.DetectedAt);
        
        if (limit.HasValue && limit.Value > 0)
        {
            orderedEvents = orderedEvents.Take(limit.Value);
        }

        var watches = (await watchRepo.GetAllAsync(ct)).ToDictionary(w => w.Id);
        
        var dtos = orderedEvents.Select(e =>
        {
            watches.TryGetValue(e.WatchedSiteId, out var watch);
            return new ChangeListItemDto
            {
                Id = e.Id.ToString(),
                WatchId = e.WatchedSiteId.ToString(),
                WatchTitle = watch?.Name,
                DetectedAt = e.DetectedAt,
                Summary = e.DiffSummary ?? "Changes detected",
                Importance = e.Importance.ToString(),
                LinesAdded = e.LinesAdded,
                LinesRemoved = e.LinesRemoved,
                IsViewed = e.IsViewed,
                IsNotified = e.IsNotified
            };
        }).ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetChangeById(
        string id,
        IRepository<ChangeEvent> eventRepo,
        IRepository<ChangeSnapshot> snapshotRepo,
        IRepository<WatchedSite> watchRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var change = await eventRepo.GetByIdAsync(guidId, ct);
        if (change == null)
            return Results.NotFound();

        var watch = await watchRepo.GetByIdAsync(change.WatchedSiteId, ct);
        var previousSnapshot = await snapshotRepo.GetByIdAsync(change.PreviousSnapshotId, ct);
        var currentSnapshot = await snapshotRepo.GetByIdAsync(change.CurrentSnapshotId, ct);

        // Generate plain text diff from HTML or use empty string
        var diffText = change.DiffHtml?.Replace("<ins>", "+").Replace("</ins>", "")
                                       .Replace("<del>", "-").Replace("</del>", "")
                                       .Replace("<br>", "\n").Replace("<br/>", "\n")
                      ?? "";

        return Results.Ok(new ChangeDetailDto
        {
            Id = change.Id.ToString(),
            WatchId = change.WatchedSiteId.ToString(),
            WatchTitle = watch?.Name,
            WatchUrl = watch?.Url,
            DetectedAt = change.DetectedAt,
            Summary = change.DiffSummary ?? "Changes detected",
            DiffText = diffText,
            DiffHtml = change.DiffHtml,
            Importance = change.Importance.ToString(),
            LinesAdded = change.LinesAdded,
            LinesRemoved = change.LinesRemoved,
            IsViewed = change.IsViewed,
            PreviousSnapshot = previousSnapshot != null ? new SnapshotInfoDto
            {
                Id = previousSnapshot.Id.ToString(),
                CapturedAt = previousSnapshot.CapturedAt,
                Content = previousSnapshot.Content,
                ScreenshotPath = previousSnapshot.ScreenshotPath
            } : null,
            CurrentSnapshot = currentSnapshot != null ? new SnapshotInfoDto
            {
                Id = currentSnapshot.Id.ToString(),
                CapturedAt = currentSnapshot.CapturedAt,
                Content = currentSnapshot.Content,
                ScreenshotPath = currentSnapshot.ScreenshotPath
            } : null
        });
    }

    private static async Task<IResult> MarkAsViewed(
        string id,
        IRepository<ChangeEvent> eventRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var change = await eventRepo.GetByIdAsync(guidId, ct);
        if (change == null)
            return Results.NotFound();

        change.IsViewed = true;
        await eventRepo.UpdateAsync(change, ct);
        
        return Results.NoContent();
    }

    private static async Task<IResult> GetUnviewedCount(
        IRepository<ChangeEvent> eventRepo,
        CancellationToken ct)
    {
        var count = await eventRepo.CountAsync(e => !e.IsViewed, ct);
        return Results.Ok(count);
    }
}
