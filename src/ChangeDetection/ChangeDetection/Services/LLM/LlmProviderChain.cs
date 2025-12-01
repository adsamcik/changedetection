using System.Diagnostics;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Polly;
using Polly.CircuitBreaker;

namespace ChangeDetection.Services.LLM;

/// <summary>
/// LLM provider chain with priority-based fallback and circuit breaker support.
/// </summary>
public class LlmProviderChain : ILlmProviderChain
{
    private readonly IRepository<LlmProviderConfig> _providerRepo;
    private readonly IRepository<LlmUsageRecord> _usageRepo;
    private readonly ILogger<LlmProviderChain> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<Guid, ResiliencePipeline> _circuitBreakers = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LlmProviderChain(
        IRepository<LlmProviderConfig> providerRepo,
        IRepository<LlmUsageRecord> usageRepo,
        ILogger<LlmProviderChain> logger,
        IServiceProvider serviceProvider)
    {
        _providerRepo = providerRepo;
        _usageRepo = usageRepo;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task<LlmResponse> ExecuteAsync(string prompt, LlmRequestOptions? options = null, CancellationToken ct = default)
    {
        options ??= new LlmRequestOptions();
        var stopwatch = Stopwatch.StartNew();
        var failedProviders = 0;

        // Get providers ordered by priority
        var providers = await GetProvidersToTryAsync(options.ProviderName, ct);
        
        if (!providers.Any())
        {
            return new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "No LLM providers available",
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }

        foreach (var provider in providers)
        {
            try
            {
                var circuitBreaker = await GetOrCreateCircuitBreakerAsync(provider);
                
                var result = await circuitBreaker.ExecuteAsync(async token =>
                {
                    return await ExecuteWithProviderAsync(provider, prompt, options, token);
                }, ct);

                if (result.IsSuccess)
                {
                    result.FailedProviderCount = failedProviders;
                    result.DurationMs = stopwatch.ElapsedMilliseconds;
                    
                    // Record usage
                    await RecordUsageAsync(provider, result, options, ct);
                    
                    return result;
                }
                
                failedProviders++;
                _logger.LogWarning("Provider {Provider} returned unsuccessful result, trying next", provider.Name);
            }
            catch (BrokenCircuitException)
            {
                failedProviders++;
                _logger.LogWarning("Provider {Provider} circuit breaker is open, skipping", provider.Name);
            }
            catch (Exception ex)
            {
                failedProviders++;
                _logger.LogError(ex, "Provider {Provider} failed with exception", provider.Name);
                
                // Update provider health
                await UpdateProviderHealthAsync(provider, false, ex.Message, ct);
            }
        }

        return new LlmResponse
        {
            IsSuccess = false,
            ErrorMessage = "All LLM providers failed",
            FailedProviderCount = failedProviders,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<IEnumerable<LlmProviderConfig>> GetProvidersToTryAsync(string? specificProvider, CancellationToken ct)
    {
        var allProviders = await _providerRepo.GetAllAsync(ct);
        var enabledProviders = allProviders
            .Where(p => p.IsEnabled && p.IsHealthy)
            .OrderBy(p => p.Priority);

        if (!string.IsNullOrEmpty(specificProvider))
        {
            var specific = enabledProviders.FirstOrDefault(p => 
                p.Name.Equals(specificProvider, StringComparison.OrdinalIgnoreCase));
            
            if (specific != null)
            {
                return [specific];
            }
        }

        return enabledProviders;
    }

    private async Task<LlmResponse> ExecuteWithProviderAsync(
        LlmProviderConfig provider, 
        string prompt, 
        LlmRequestOptions options,
        CancellationToken ct)
    {
        var providerStopwatch = Stopwatch.StartNew();
        
        var kernel = CreateKernelForProvider(provider);
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(prompt);

        var settings = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = options.Temperature,
                ["max_tokens"] = options.MaxTokens
            }
        };

        var response = await chatService.GetChatMessageContentAsync(chatHistory, settings, kernel, ct);
        
        providerStopwatch.Stop();

        var content = response.Content ?? "";
        
        // Estimate tokens (rough approximation)
        var inputTokens = (int)(prompt.Length / 4.0);
        var outputTokens = (int)(content.Length / 4.0);
        var cost = CalculateCost(provider, inputTokens, outputTokens);

        return new LlmResponse
        {
            IsSuccess = true,
            Content = content,
            ProviderUsed = provider.Name,
            Model = provider.Model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = cost,
            DurationMs = providerStopwatch.ElapsedMilliseconds
        };
    }

    private Kernel CreateKernelForProvider(LlmProviderConfig provider)
    {
        var builder = Kernel.CreateBuilder();

        switch (provider.ProviderType)
        {
            case LlmProviderType.OpenAI:
                builder.AddOpenAIChatCompletion(
                    modelId: provider.Model,
                    apiKey: provider.ApiKey ?? throw new InvalidOperationException("OpenAI API key is required"));
                break;

            case LlmProviderType.AzureOpenAI:
                builder.AddAzureOpenAIChatCompletion(
                    deploymentName: provider.Model,
                    endpoint: provider.Endpoint ?? throw new InvalidOperationException("Azure OpenAI endpoint is required"),
                    apiKey: provider.ApiKey ?? throw new InvalidOperationException("Azure OpenAI API key is required"));
                break;

            case LlmProviderType.Ollama:
                // For Ollama, we use the OpenAI connector with custom endpoint
                var endpoint = provider.Endpoint ?? "http://localhost:11434";
#pragma warning disable SKEXP0010
                builder.AddOpenAIChatCompletion(
                    modelId: provider.Model,
                    apiKey: "ollama", // Ollama doesn't require API key but SK requires non-null
                    endpoint: new Uri($"{endpoint}/v1"));
#pragma warning restore SKEXP0010
                break;

            case LlmProviderType.Gemini:
#pragma warning disable SKEXP0070
                builder.AddGoogleAIGeminiChatCompletion(
                    modelId: provider.Model,
                    apiKey: provider.ApiKey ?? throw new InvalidOperationException("Gemini API key is required"));
#pragma warning restore SKEXP0070
                break;

            case LlmProviderType.Claude:
                // Claude support via OpenAI-compatible endpoint or custom connector
                // For now, using OpenAI connector with Anthropic endpoint
#pragma warning disable SKEXP0010
                builder.AddOpenAIChatCompletion(
                    modelId: provider.Model,
                    apiKey: provider.ApiKey ?? throw new InvalidOperationException("Claude API key is required"),
                    endpoint: new Uri(provider.Endpoint ?? "https://api.anthropic.com/v1"));
#pragma warning restore SKEXP0010
                break;

            default:
                throw new NotSupportedException($"Provider type {provider.ProviderType} is not supported");
        }

        return builder.Build();
    }

    private async Task<ResiliencePipeline> GetOrCreateCircuitBreakerAsync(LlmProviderConfig provider)
    {
        await _lock.WaitAsync();
        try
        {
            if (_circuitBreakers.TryGetValue(provider.Id, out var existing))
            {
                return existing;
            }

            var pipeline = new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromMinutes(1),
                    MinimumThroughput = 3,
                    BreakDuration = TimeSpan.FromMinutes(5),
                    OnOpened = args =>
                    {
                        _logger.LogWarning("Circuit breaker opened for provider {Provider}", provider.Name);
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = args =>
                    {
                        _logger.LogInformation("Circuit breaker closed for provider {Provider}", provider.Name);
                        return ValueTask.CompletedTask;
                    }
                })
                .AddRetry(new Polly.Retry.RetryStrategyOptions
                {
                    MaxRetryAttempts = provider.MaxRetries,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    OnRetry = args =>
                    {
                        _logger.LogWarning("Retrying provider {Provider}, attempt {Attempt}", 
                            provider.Name, args.AttemptNumber);
                        return ValueTask.CompletedTask;
                    }
                })
                .AddTimeout(TimeSpan.FromSeconds(provider.TimeoutSeconds))
                .Build();

            _circuitBreakers[provider.Id] = pipeline;
            return pipeline;
        }
        finally
        {
            _lock.Release();
        }
    }

    private decimal CalculateCost(LlmProviderConfig provider, int inputTokens, int outputTokens)
    {
        var inputCost = (inputTokens / 1000m) * provider.CostPer1KInputTokens;
        var outputCost = (outputTokens / 1000m) * provider.CostPer1KOutputTokens;
        return inputCost + outputCost;
    }

    private async Task UpdateProviderHealthAsync(LlmProviderConfig provider, bool isHealthy, string? error, CancellationToken ct)
    {
        provider.IsHealthy = isHealthy;
        provider.LastError = error;
        provider.LastErrorAt = isHealthy ? null : DateTime.UtcNow;
        provider.UpdatedAt = DateTime.UtcNow;
        
        await _providerRepo.UpdateAsync(provider, ct);
    }

    private async Task RecordUsageAsync(LlmProviderConfig provider, LlmResponse response, LlmRequestOptions options, CancellationToken ct)
    {
        var record = new LlmUsageRecord
        {
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            Model = response.Model ?? provider.Model,
            UsageType = options.UsageType,
            WatchedSiteId = options.WatchedSiteId,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            Cost = response.Cost,
            DurationMs = response.DurationMs,
            IsSuccess = response.IsSuccess,
            ErrorMessage = response.ErrorMessage
        };

        await _usageRepo.InsertAsync(record, ct);

        // Update provider totals
        provider.TotalTokensUsed += response.InputTokens + response.OutputTokens;
        provider.TotalCost += response.Cost;
        provider.UpdatedAt = DateTime.UtcNow;
        
        await _providerRepo.UpdateAsync(provider, ct);
    }

    public async Task<IEnumerable<LlmProviderConfig>> GetAvailableProvidersAsync(CancellationToken ct = default)
    {
        var providers = await _providerRepo.GetAllAsync(ct);
        return providers.Where(p => p.IsEnabled).OrderBy(p => p.Priority);
    }

    public async Task<IEnumerable<ProviderHealthStatus>> GetHealthStatusAsync(CancellationToken ct = default)
    {
        var providers = await _providerRepo.GetAllAsync(ct);
        
        return providers.Select(p => new ProviderHealthStatus
        {
            ProviderName = p.Name,
            IsHealthy = p.IsHealthy,
            IsEnabled = p.IsEnabled,
            Priority = p.Priority,
            LastError = p.LastError,
            LastErrorAt = p.LastErrorAt
        });
    }
}
