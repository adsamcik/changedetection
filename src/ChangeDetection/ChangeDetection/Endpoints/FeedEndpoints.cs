using System.Text;
using System.Xml.Linq;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ChangeDetection.Endpoints;

public static class FeedEndpoints
{
    public static RouteGroupBuilder MapFeedEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{watchId}/history", GetChangeHistory)
            .WithName("GetChangeHistory")
            .Produces<ChangeHistoryResponse>();

        group.MapGet("/{watchId}/export/csv", ExportCsv)
            .WithName("ExportCsv")
            .Produces<FileContentHttpResult>(contentType: "text/csv");

        group.MapGet("/{watchId}/feed.rss", GetRssFeed)
            .WithName("GetRssFeed")
            .Produces<ContentHttpResult>(contentType: "application/rss+xml");

        return group;
    }

    internal static async Task<IResult> GetChangeHistory(
        string watchId,
        IRepository<ChangeEvent> eventRepo,
        IRepository<WatchedSite> watchRepo,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        if (!Guid.TryParse(watchId, out var id))
            return TypedResults.BadRequest("Invalid watch ID");

        var watch = await watchRepo.GetByIdAsync(id, ct);
        if (watch is null)
            return TypedResults.NotFound($"Watch {watchId} not found");

        var pageSize = Math.Clamp(limit ?? 20, 1, 100);

        var events = await eventRepo.FindAsync(e => e.WatchedSiteId == id, ct);
        var ordered = events.OrderByDescending(e => e.DetectedAt).ToList();

        if (cursor is not null && DateTime.TryParse(cursor, out var cursorDate))
            ordered = ordered.Where(e => e.DetectedAt < cursorDate).ToList();

        var page = ordered.Take(pageSize).ToList();
        var hasMore = ordered.Count > pageSize;

        return TypedResults.Ok(new ChangeHistoryResponse
        {
            Items = page.Select(e => new ChangeHistoryItem
            {
                Id = e.Id,
                DetectedAt = e.DetectedAt,
                ChangeType = e.ChangeType.ToString(),
                Importance = e.Importance.ToString(),
                Summary = e.BriefSummary ?? e.DiffSummary,
                LinesAdded = e.LinesAdded,
                LinesRemoved = e.LinesRemoved
            }).ToList(),
            HasMore = hasMore,
            NextCursor = hasMore ? page.Last().DetectedAt.ToString("O") : null
        });
    }

    internal static async Task<IResult> ExportCsv(
        string watchId,
        IRepository<ChangeEvent> eventRepo,
        IRepository<WatchedSite> watchRepo,
        string? since,
        CancellationToken ct)
    {
        if (!Guid.TryParse(watchId, out var id))
            return TypedResults.BadRequest("Invalid watch ID");

        var watch = await watchRepo.GetByIdAsync(id, ct);
        if (watch is null)
            return TypedResults.NotFound($"Watch {watchId} not found");

        var events = await eventRepo.FindAsync(e => e.WatchedSiteId == id, ct);
        var ordered = events.OrderByDescending(e => e.DetectedAt);

        if (since is not null && DateTime.TryParse(since, out var sinceDate))
            ordered = ordered.Where(e => e.DetectedAt >= sinceDate).OrderByDescending(e => e.DetectedAt);

        var sb = new StringBuilder();
        sb.AppendLine("DetectedAt,ChangeType,Importance,Summary,LinesAdded,LinesRemoved");
        foreach (var e in ordered)
        {
            var summary = (e.BriefSummary ?? e.DiffSummary ?? "").Replace("\"", "\"\"");
            sb.AppendLine($"\"{e.DetectedAt:O}\",\"{e.ChangeType}\",\"{e.Importance}\",\"{summary}\",{e.LinesAdded},{e.LinesRemoved}");
        }

        return TypedResults.Text(sb.ToString(), "text/csv");
    }

    internal static async Task<IResult> GetRssFeed(
        string watchId,
        IRepository<ChangeEvent> eventRepo,
        IRepository<WatchedSite> watchRepo,
        HttpContext httpContext,
        int? limit,
        CancellationToken ct)
    {
        if (!Guid.TryParse(watchId, out var id))
            return TypedResults.BadRequest("Invalid watch ID");

        var watch = await watchRepo.GetByIdAsync(id, ct);
        if (watch is null)
            return TypedResults.NotFound($"Watch {watchId} not found");

        var pageSize = Math.Clamp(limit ?? 50, 1, 100);
        var events = await eventRepo.FindAsync(e => e.WatchedSiteId == id, ct);
        var items = events.OrderByDescending(e => e.DetectedAt).Take(pageSize);

        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var rss = new XDocument(
            new XElement("rss",
                new XAttribute("version", "2.0"),
                new XElement("channel",
                    new XElement("title", $"Changes: {watch.Name ?? watch.Url}"),
                    new XElement("link", watch.Url),
                    new XElement("description", $"Change feed for {watch.Url}"),
                    items.Select(e =>
                        new XElement("item",
                            new XElement("title", e.BriefSummary ?? $"{e.ChangeType} detected"),
                            new XElement("description", e.DiffSummary ?? ""),
                            new XElement("pubDate", e.DetectedAt.ToString("R")),
                            new XElement("guid", $"{baseUrl}/api/changes/{e.Id}"))))));

        return TypedResults.Text(rss.ToString(), "application/rss+xml");
    }
}

public class ChangeHistoryResponse
{
    public List<ChangeHistoryItem> Items { get; set; } = [];
    public bool HasMore { get; set; }
    public string? NextCursor { get; set; }
}

public class ChangeHistoryItem
{
    public Guid Id { get; set; }
    public DateTime DetectedAt { get; set; }
    public string ChangeType { get; set; } = "";
    public string Importance { get; set; } = "";
    public string? Summary { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
}
