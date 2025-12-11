using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Startup;

/// <summary>
/// Seeds a default Ollama LLM provider on application startup if none exists.
/// This ensures the app works out-of-the-box when Ollama is running locally.
/// </summary>
public class DefaultProviderSeeder(
    IServiceProvider serviceProvider,
    ILogger<DefaultProviderSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var providerRepo = scope.ServiceProvider.GetRequiredService<IRepository<LlmProviderConfig>>();

        try
        {
            var providers = await providerRepo.GetAllAsync(cancellationToken);
            
            if (providers.Any())
            {
                logger.LogDebug("LLM providers already configured, skipping seed");
                return;
            }

            // Seed default Ollama provider
            var ollamaProvider = new LlmProviderConfig
            {
                Id = Guid.NewGuid(),
                Name = "Ollama Local",
                ProviderType = LlmProviderType.Ollama,
                Endpoint = "http://localhost:11434",
                Model = "ministral-3:8b",
                IsEnabled = true,
                Priority = 1,
                TimeoutSeconds = 300,
                MaxRetries = 3,
                CreatedAt = DateTime.UtcNow
            };

            await providerRepo.InsertAsync(ollamaProvider, cancellationToken);
            
            logger.LogInformation(
                "Seeded default Ollama provider '{Name}' with model '{Model}' at {Endpoint}",
                ollamaProvider.Name,
                ollamaProvider.Model,
                ollamaProvider.Endpoint);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to seed default LLM provider. Users can configure providers manually.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
