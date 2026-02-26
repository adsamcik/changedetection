using ChangeDetection.Services.Background;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for database health monitoring.
/// </summary>
public static class DatabaseHealthEndpoints
{
    public static RouteGroupBuilder MapDatabaseHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/database", GetDatabaseHealth)
            .WithName("GetDatabaseHealth")
            .Produces<DatabaseHealthInfo>();

        return group;
    }

    private static IResult GetDatabaseHealth(DatabaseMaintenanceService maintenanceService)
    {
        var info = maintenanceService.GetHealthInfo();
        return Results.Ok(info);
    }
}
