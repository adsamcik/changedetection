using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Startup;

/// <summary>
/// Syncs LLM provider configuration from appsettings.json to the database on startup.
/// Config-defined providers are created or updated automatically.
/// </summary>
public class LlmProviderConfigSyncService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<LlmProviderConfigSyncService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var providers = configuration
            .GetSection(LlmProviders.SectionName)
            .Get<List<LlmProviderOption>>() ?? [];

        if (providers.Count == 0)
        {
            logger.LogDebug("No LLM providers configured in appsettings.json");
            return;
        }

        // Validate all entries upfront
        var allErrors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            allErrors.AddRange(p.Validate());
            if (!names.Add(p.Name))
                allErrors.Add($"Duplicate provider name '{p.Name}'");
        }

        if (allErrors.Count > 0)
        {
            foreach (var error in allErrors)
                logger.LogError("LLM provider config error: {Error}", error);
            logger.LogError("LLM provider configuration has {Count} error(s). Skipping sync", allErrors.Count);
            return;
        }

        using var scope = serviceProvider.CreateScope();
        var providerRepo = scope.ServiceProvider.GetRequiredService<IRepository<LlmProviderConfig>>();

        try
        {
            var existing = (await providerRepo.GetAllAsync(cancellationToken)).ToList();
            var existingByName = existing.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < providers.Count; i++)
            {
                var option = providers[i];

                if (existingByName.TryGetValue(option.Name, out var db))
                {
                    ApplyOption(db, option, priority: i);
                    db.UpdatedAt = DateTime.UtcNow;
                    await providerRepo.UpdateAsync(db, cancellationToken);
                    logger.LogInformation("Synced LLM provider '{Name}' ({Type}, {Model})",
                        db.Name, db.ProviderType, db.Model);
                }
                else
                {
                    var newProvider = new LlmProviderConfig
                    {
                        Name = option.Name,
                        Model = option.Model,
                        IsHealthy = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    ApplyOption(newProvider, option, priority: i);
                    await providerRepo.InsertAsync(newProvider, cancellationToken);
                    logger.LogInformation("Created LLM provider '{Name}' ({Type}, {Model})",
                        newProvider.Name, newProvider.ProviderType, newProvider.Model);
                }
            }

            logger.LogInformation("LLM provider sync complete: {Count} provider(s)", providers.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync LLM provider configuration");
        }
    }

    private static void ApplyOption(LlmProviderConfig target, LlmProviderOption source, int priority)
    {
        target.ProviderType = source.Type;
        target.Model = source.Model;
        target.Endpoint = source.Endpoint;
        target.IsEnabled = source.Enabled;
        target.TimeoutSeconds = source.TimeoutSeconds;
        target.MaxRetries = source.MaxRetries;
        target.Priority = priority;
        target.CostPer1KInputTokens = source.CostPer1KInput;
        target.CostPer1KOutputTokens = source.CostPer1KOutput;

        // Only overwrite API key if explicitly set in config
        if (!string.IsNullOrEmpty(source.ApiKey))
            target.ApiKey = source.ApiKey;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
