using ChangeDetection.Services.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChangeDetection.Services.Startup;

/// <summary>
/// Health check that verifies LiteDB is accessible.
/// </summary>
public class LiteDbHealthCheck(LiteDbContext dbContext) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var collections = dbContext.Database.GetCollectionNames().ToList();
            return Task.FromResult(HealthCheckResult.Healthy(
                $"LiteDB is responsive with {collections.Count} collections"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("LiteDB is not responsive", ex));
        }
    }
}
