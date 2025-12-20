using System.Text.Json;
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
    // Preferred models in order of preference
    private static readonly string[] PreferredModels = ["ministral-3:14b", "ministral-3:8b", "llama3.1"];
    
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

            // Detect best available Ollama model
            var (endpoint, model) = await DetectBestOllamaModelAsync(cancellationToken);
            
            if (model is null)
            {
                logger.LogInformation("No Ollama instance detected, skipping provider seed");
                return;
            }

            // Seed default Ollama provider with detected model
            var ollamaProvider = new LlmProviderConfig
            {
                Id = Guid.NewGuid(),
                Name = "Ollama Local",
                ProviderType = LlmProviderType.Ollama,
                Endpoint = endpoint,
                Model = model,
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

    private async Task<(string Endpoint, string? Model)> DetectBestOllamaModelAsync(CancellationToken ct)
    {
        const string ollamaEndpoint = "http://localhost:11434";
        
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"{ollamaEndpoint}/api/tags", ct);
            
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Ollama not available at {Endpoint}", ollamaEndpoint);
                return (ollamaEndpoint, null);
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
            var models = JsonDocument.Parse(json);
            
            string? modelToUse = null;
            string? firstAvailable = null;
            
            // Collect all available model names
            List<string> availableModels = [];
            if (models.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var nameElement))
                    {
                        var name = nameElement.GetString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            availableModels.Add(name);
                            firstAvailable ??= name;
                        }
                    }
                }
            }
            
            logger.LogDebug("Detected Ollama models: {Models}", string.Join(", ", availableModels));
            
            // Find best preferred model
            foreach (var preferred in PreferredModels)
            {
                if (availableModels.Contains(preferred))
                {
                    modelToUse = preferred;
                    break;
                }
            }
            
            // Fallback to first available
            modelToUse ??= firstAvailable;
            
            return (ollamaEndpoint, modelToUse);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to detect Ollama models");
            return (ollamaEndpoint, null);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
