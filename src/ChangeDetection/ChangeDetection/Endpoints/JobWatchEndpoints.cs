using ChangeDetection.Services.JobWatch;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for the Job Watch feature — creates and manages a job search project
/// with portal watches, candidate profiles, and profile-based filtering.
/// </summary>
public static class JobWatchEndpoints
{
    public static void MapJobWatchEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/jobwatch")
            .WithTags("JobWatch");

        group.MapPost("/seed", SeedJobWatch)
            .WithName("SeedJobWatch")
            .WithDescription("Create a complete job watch project with all portal configurations");

        group.MapGet("/portals", GetPortalDefinitions)
            .WithName("GetPortalDefinitions")
            .WithDescription("List all configured portal definitions and their schemas");
    }

    private static async Task<IResult> SeedJobWatch(
        JobWatchSeedRequest request,
        JobWatchSeeder seeder,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileJson))
            return Results.BadRequest("ProfileJson is required");

        var group = await seeder.SeedAsync(
            request.ProfileJson,
            request.UserIntent ?? "Monitor biotech/life-science job portals for matching positions",
            ct);

        return Results.Created($"/api/groups/{group.Id}", new
        {
            GroupId = group.Id.ToString(),
            group.Name,
            PortalCount = JobWatchSeeder.GetAllPortalDefinitions().Count
        });
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
