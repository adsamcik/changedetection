using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.JobWatch;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for the Job Watch feature — creates and manages a job search project
/// with portal watches, candidate profiles, and profile-based filtering.
/// </summary>
public static class JobWatchEndpoints
{
    private const int MaxProfileJsonLength = 65_536; // 64KB
    private static readonly SemaphoreSlim _seedLock = new(1, 1);

    public static RouteGroupBuilder MapJobWatchEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/seed", SeedJobWatch)
            .WithName("SeedJobWatch")
            .WithDescription("Create a complete job watch project with all portal configurations");

        group.MapGet("/portals", GetPortalDefinitions)
            .WithName("GetPortalDefinitions")
            .WithDescription("List all configured portal definitions and their schemas");

        return group;
    }

    private static async Task<IResult> SeedJobWatch(
        JobWatchSeedRequest request,
        JobWatchSeeder seeder,
        IWatchGroupService groupService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileJson))
            return Results.BadRequest("ProfileJson is required");

        if (request.ProfileJson.Length > MaxProfileJsonLength)
            return Results.BadRequest($"ProfileJson exceeds maximum length of {MaxProfileJsonLength} characters");

        // Validate JSON is parseable with depth limit
        try
        {
            using var doc = JsonDocument.Parse(request.ProfileJson, new JsonDocumentOptions { MaxDepth = 10 });
        }
        catch (JsonException)
        {
            return Results.BadRequest("ProfileJson is not valid JSON");
        }

        // Serialize access to prevent duplicate seed race condition
        if (!await _seedLock.WaitAsync(TimeSpan.FromSeconds(10), ct))
            return Results.Conflict(new { Message = "A seed operation is already in progress. Please wait." });

        try
        {
            // Idempotency: check if a job watch group already exists (inside lock)
            var existingGroups = await groupService.GetAllAsync(ct);
            var existing = existingGroups.FirstOrDefault(g =>
                g.Tags.Contains("job-search") && g.AnalysisProfileJson is not null);
            if (existing is not null)
            {
                return Results.Conflict(new
                {
                    Message = "A job watch project already exists. Delete the existing group first or update its profile via PUT /api/groups/{id}.",
                    ExistingGroupId = existing.Id.ToString(),
                    existing.Name
                });
            }

            var (group, createdCount) = await seeder.SeedAsync(
                request.ProfileJson,
                request.UserIntent ?? "Monitor biotech/life-science job portals for matching positions",
                ct);

            return Results.Created($"/api/groups/{group.Id}", new
            {
                GroupId = group.Id.ToString(),
                group.Name,
                PortalCount = createdCount
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private static IResult GetPortalDefinitions()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();
        return Results.Ok(portals.Select(p => new
        {
            p.Id,
            p.Name,
            p.Description,
            p.Url,
            p.Tier,
            CheckIntervalHours = p.CheckInterval.TotalHours,
            NeedsJavaScript = p.FetchSettings.UseJavaScript,
            SchemaFields = p.Schema.Fields.Select(f => new { f.Name, Type = f.Type.ToString() })
        }));
    }
}

public class JobWatchSeedRequest
{
    public required string ProfileJson { get; set; }
    public string? UserIntent { get; set; }
}
