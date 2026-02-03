using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Startup;

/// <summary>
/// Resets LLM provider health status on startup.
/// Since Polly circuit breakers are recreated fresh on each startup,
/// persisted "unhealthy" state from previous sessions is stale and should be reset.
/// </summary>
public class LlmProviderHealthRecoveryService(
    IServiceProvider serviceProvider,
    ILogger<LlmProviderHealthRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var providerRepo = scope.ServiceProvider.GetRequiredService<IRepository<LlmProviderConfig>>();

        try
        {
            // Find all providers marked as unhealthy
            var unhealthyProviders = await providerRepo.FindAsync(
                p => !p.IsHealthy,
                cancellationToken);

            var unhealthyList = unhealthyProviders.ToList();

            if (unhealthyList.Count == 0)
            {
                logger.LogDebug("No LLM providers with stale unhealthy status");
                return;
            }

            logger.LogInformation(
                "Resetting {Count} LLM providers from unhealthy to healthy (circuit breakers are fresh on startup)",
                unhealthyList.Count);

            foreach (var provider in unhealthyList)
            {
                provider.IsHealthy = true;
                provider.LastError = null;
                provider.UpdatedAt = DateTime.UtcNow;
                await providerRepo.UpdateAsync(provider, cancellationToken);

                logger.LogDebug(
                    "Reset provider '{Name}' ({Id}) health status",
                    provider.Name,
                    provider.Id);
            }

            logger.LogInformation("LLM provider health recovery complete");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover LLM provider health during startup");
            // Don't rethrow - we don't want to prevent app startup due to recovery failure
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
