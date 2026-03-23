using System.Diagnostics;
using System.Runtime.CompilerServices;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

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
    private readonly ILlmLogService _llmLog;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IReadOnlyDictionary<LlmProviderType, ILlmKernelFactory> _factories;
    private readonly Dictionary<Guid, ResiliencePipeline> _circuitBreakers = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LlmProviderChain(
        IRepository<LlmProviderConfig> providerRepo,
        IRepository<LlmUsageRecord> usageRepo,
        ILogger<LlmProviderChain> logger,
        IServiceProvider serviceProvider,
        ILlmLogService llmLogService,
        IEnumerable<ILlmKernelFactory> factories,
        IHttpClientFactory? httpClientFactory = null)
    {
        _providerRepo = providerRepo;
        _usageRepo = usageRepo;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _llmLog = llmLogService;
        _factories = factories.ToDictionary(f => f.ProviderType);
        _httpClientFactory = httpClientFactory;
    }

    public async Task<LlmResponse> ExecuteAsync(string prompt, LlmRequestOptions? options = null, CancellationToken ct = default)
    {
        options ??= new LlmRequestOptions();
        var stopwatch = Stopwatch.StartNew();
        var failedProviders = 0;

        // Get providers ordered by priority
        _logger.LogWarning("LlmProviderChain.ExecuteAsync: entering GetProvidersToTryAsync (thread {ThreadId})",
            Environment.CurrentManagedThreadId);
        var providers = await GetProvidersToTryAsync(options.ProviderName, ct, options.PreferLargeModel);
        _logger.LogWarning("LlmProviderChain.ExecuteAsync: got {Count} providers in {Elapsed}ms",
            providers.Count(), stopwatch.ElapsedMilliseconds);
        
        if (!providers.Any())
        {
            return new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "No LLM providers available",
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }

        // Apply compact mode adjustments for small models
        var firstProvider = providers.First();
        var effectiveOptions = ApplyCompactModeIfNeeded(options, firstProvider);

        foreach (var provider in providers)
        {
            Guid? requestId = null;
            try
            {
                // Log the request attempt and capture correlation ID
                requestId = _llmLog.LogRequest(provider.Name, provider.Model, prompt);
                
                var circuitBreaker = await GetOrCreateCircuitBreakerAsync(provider);
                
                var result = await circuitBreaker.ExecuteAsync(async token =>
                {
                    return await ExecuteWithProviderAsync(provider, prompt, effectiveOptions, token);
                }, ct);

                if (result.IsSuccess)
                {
                    result.FailedProviderCount = failedProviders;
                    result.DurationMs = stopwatch.ElapsedMilliseconds;
                    
                    // Log successful response with correlation ID
                    _llmLog.LogResponse(
                        provider.Name,
                        provider.Model,
                        result.Content ?? "",
                        result.DurationMs,
                        result.InputTokens,
                        result.OutputTokens,
                        requestId);
                    
                    // Restore health if previously unhealthy
                    if (!provider.IsHealthy)
                    {
                        await UpdateProviderHealthAsync(provider, isHealthy: true, null, ct);
                    }
                    
                    // Record usage
                    await RecordUsageAsync(provider, result, effectiveOptions, ct);
                    
                    return result;
                }
                
                failedProviders++;
                _logger.LogWarning("Provider {Provider} returned unsuccessful result, trying next", provider.Name);
                
                // Log unsuccessful result and try fallback
                var nextProvider = providers.Skip(failedProviders).FirstOrDefault();
                if (nextProvider != null)
                {
                    _llmLog.LogFallback(provider.Name, nextProvider.Name, "Provider returned unsuccessful result");
                }
            }
            catch (BrokenCircuitException)
            {
                failedProviders++;
                _logger.LogWarning("Provider {Provider} circuit breaker is open, skipping", provider.Name);
                _llmLog.LogCircuitBreakerBlocked(provider.Name);
            }
            catch (Exception ex)
            {
                failedProviders++;
                _logger.LogError(ex, "Provider {Provider} failed with exception", provider.Name);
                _llmLog.LogError(provider.Name, provider.Model, ex, prompt, requestId);
                
                // Update provider health
                await UpdateProviderHealthAsync(provider, false, ex.Message, ct);
                
                // Log fallback if there's a next provider
                var nextProvider = providers.Skip(failedProviders).FirstOrDefault();
                if (nextProvider != null)
                {
                    _llmLog.LogFallback(provider.Name, nextProvider.Name, ex.Message);
                }
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

    public async IAsyncEnumerable<LlmStreamChunk> ExecuteStreamingAsync(
        string prompt, 
        LlmRequestOptions? options = null, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new LlmRequestOptions();
        var stopwatch = Stopwatch.StartNew();
        var failedProviders = 0;

        // Get providers ordered by priority
        var providers = await GetProvidersToTryAsync(options.ProviderName, ct, options.PreferLargeModel);
        
        if (!providers.Any())
        {
            yield return new LlmStreamChunk
            {
                Type = LlmStreamChunkType.Error,
                ErrorMessage = "No LLM providers available"
            };
            yield break;
        }

        // Apply compact mode adjustments for small models
        var firstProvider = providers.First();
        var effectiveOptions = ApplyCompactModeIfNeeded(options, firstProvider);

        foreach (var provider in providers)
        {
            // Get the circuit breaker for this provider
            var circuitBreaker = await GetOrCreateCircuitBreakerAsync(provider);
            
            // Collect chunks in a channel to avoid yield in try-catch
            var channel = System.Threading.Channels.Channel.CreateUnbounded<LlmStreamChunk>();
            var streamingSuccess = false;
            Exception? providerException = null;
            
            // Start streaming in background task
            var streamTask = Task.Run(async () =>
            {
                try
                {
                    // Execute the streaming through the circuit breaker pipeline
                    // This wraps the entire streaming operation with retry, timeout, and circuit breaker
                    await circuitBreaker.ExecuteAsync(async (pipelineCt) =>
                    {
                        channel.Writer.TryWrite(new LlmStreamChunk
                        {
                            Type = LlmStreamChunkType.Start,
                            ProviderName = provider.Name,
                            Model = provider.Model
                        });

                        var contentBuilder = new System.Text.StringBuilder();
                        
                        await foreach (var chunk in ExecuteStreamingWithProviderAsync(provider, prompt, effectiveOptions, pipelineCt))
                        {
                            if (chunk.Type == LlmStreamChunkType.Content && chunk.Text != null)
                            {
                                contentBuilder.Append(chunk.Text);
                            }
                            channel.Writer.TryWrite(chunk);
                        }

                        // Build final response
                        var content = contentBuilder.ToString();
                        var inputTokens = (int)(prompt.Length / 4.0);
                        var outputTokens = (int)(content.Length / 4.0);
                        var cost = CalculateCost(provider, inputTokens, outputTokens);

                        var finalResponse = new LlmResponse
                        {
                            IsSuccess = true,
                            Content = content,
                            ProviderUsed = provider.Name,
                            Model = provider.Model,
                            InputTokens = inputTokens,
                            OutputTokens = outputTokens,
                            Cost = cost,
                            DurationMs = stopwatch.ElapsedMilliseconds
                        };

                        // Restore health if previously unhealthy
                        if (!provider.IsHealthy)
                        {
                            await UpdateProviderHealthAsync(provider, isHealthy: true, null, pipelineCt);
                        }
                        
                        // Record usage
                        await RecordUsageAsync(provider, finalResponse, effectiveOptions, pipelineCt);

                        channel.Writer.TryWrite(new LlmStreamChunk
                        {
                            Type = LlmStreamChunkType.Complete,
                            FinalResponse = finalResponse
                        });
                        
                        streamingSuccess = true;
                    }, ct);
                }
                catch (BrokenCircuitException)
                {
                    _logger.LogWarning("Provider {Provider} circuit breaker is open, skipping", provider.Name);
                    _llmLog.LogCircuitBreakerBlocked(provider.Name);
                    providerException = new BrokenCircuitException();
                }
                catch (TimeoutRejectedException ex)
                {
                    _logger.LogWarning("Provider {Provider} timed out during streaming", provider.Name);
                    _llmLog.LogError(provider.Name, provider.Model, ex, prompt);
                    providerException = ex;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Provider {Provider} failed with exception during streaming", provider.Name);
                    _llmLog.LogError(provider.Name, provider.Model, ex, prompt);
                    providerException = ex;
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            // Yield chunks as they come in
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            {
                yield return chunk;
            }

            // Await completion with timeout to prevent hanging if the stream task is stuck
            using var streamCompletionCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await streamTask.WaitAsync(streamCompletionCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Stream task for provider {Provider} did not complete within 30s after channel closed", 
                    provider.Name);
            }

            if (streamingSuccess)
            {
                yield break; // Success, don't try other providers
            }

            // Handle failure
            failedProviders++;
            if (providerException != null && providerException is not BrokenCircuitException)
            {
                await UpdateProviderHealthAsync(provider, false, providerException.Message, ct);
                
                // Log fallback if there's a next provider
                var nextProvider = providers.Skip(failedProviders).FirstOrDefault();
                if (nextProvider != null)
                {
                    _llmLog.LogFallback(provider.Name, nextProvider.Name, providerException.Message);
                }
            }
        }

        yield return new LlmStreamChunk
        {
            Type = LlmStreamChunkType.Error,
            ErrorMessage = "All LLM providers failed"
        };
    }

    private async IAsyncEnumerable<LlmStreamChunk> ExecuteStreamingWithProviderAsync(
        LlmProviderConfig provider,
        string prompt,
        LlmRequestOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
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

        await foreach (var content in chatService.GetStreamingChatMessageContentsAsync(chatHistory, settings, kernel, ct))
        {
            if (!string.IsNullOrEmpty(content.Content))
            {
                yield return new LlmStreamChunk
                {
                    Type = LlmStreamChunkType.Content,
                    Text = content.Content,
                    ProviderName = provider.Name,
                    Model = provider.Model
                };
            }
        }
    }

    private async Task<IEnumerable<LlmProviderConfig>> GetProvidersToTryAsync(string? specificProvider, CancellationToken ct, bool preferLargeModel = false)
    {
        var allProviders = await _providerRepo.GetAllAsync(ct);
        // Include unhealthy providers so Polly circuit breaker can attempt half-open recovery
        var enabledProviders = allProviders
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.IsHealthy ? 0 : 1) // Healthy providers first, unhealthy last
            .ThenBy(p => p.Priority)
            .ToList();

        if (!string.IsNullOrEmpty(specificProvider))
        {
            var specific = enabledProviders.FirstOrDefault(p => 
                p.Name.Equals(specificProvider, StringComparison.OrdinalIgnoreCase));
            
            if (specific != null)
            {
                return [specific];
            }
        }

        // When PreferLargeModel is set, sort non-small models first while preserving priority order within each group
        if (preferLargeModel && enabledProviders.Count > 1)
        {
            enabledProviders = enabledProviders
                .OrderBy(p => IsSmallModel(p.Model) ? 1 : 0)
                .ThenBy(p => p.Priority)
                .ToList();
        }

        // If no providers configured, try auto-detecting Ollama
        if (enabledProviders.Count == 0)
        {
            var ollamaProvider = await TryAutoDetectOllamaAsync(ct);
            if (ollamaProvider != null)
            {
                return [ollamaProvider];
            }
        }

        return enabledProviders;
    }

    private async Task<LlmProviderConfig?> TryAutoDetectOllamaAsync(CancellationToken ct)
    {
        const string ollamaEndpoint = "http://localhost:11434";
        // Preferred models in order of preference
        string[] preferredModels = ["ministral-3:14b", "ministral-3:8b", "llama3.1"];
        
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"{ollamaEndpoint}/api/tags", ct);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Ollama not available at {Endpoint}", ollamaEndpoint);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
            var models = System.Text.Json.JsonDocument.Parse(json);
            
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
            
            // Find best preferred model
            foreach (var preferred in preferredModels)
            {
                if (availableModels.Contains(preferred))
                {
                    modelToUse = preferred;
                    break;
                }
            }
            
            // Fallback to first available
            modelToUse ??= firstAvailable;
            
            if (string.IsNullOrEmpty(modelToUse))
            {
                _logger.LogDebug("No models found in Ollama");
                return null;
            }
            
            _logger.LogInformation("Auto-detected Ollama with model {Model}", modelToUse);
            
            return new LlmProviderConfig
            {
                Id = Guid.Empty, // Ephemeral provider, not persisted
                Name = "Ollama (Auto-detected)",
                ProviderType = LlmProviderType.Ollama,
                Endpoint = ollamaEndpoint,
                Model = modelToUse,
                Priority = 999,
                IsEnabled = true,
                IsHealthy = true,
                TimeoutSeconds = 120, // 2 minutes per request for local models
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not auto-detect Ollama");
            return null;
        }
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
        // Create HttpClient with the provider's configured timeout
        // Default HttpClient has 100s timeout which is too short for some LLM operations
        // Use IHttpClientFactory if available (allows test mocking), otherwise create directly
        HttpClient httpClient;
        if (_httpClientFactory != null)
        {
            httpClient = _httpClientFactory.CreateClient("LlmProvider");
            httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        }
        else
        {
            httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
            };
        }

        // Use registered factory for the provider type
        if (!_factories.TryGetValue(provider.ProviderType, out var factory))
        {
            throw new NotSupportedException(
                $"Provider type {provider.ProviderType} is not supported. " +
                $"Available factories: {string.Join(", ", _factories.Keys)}");
        }

        return factory.CreateKernel(provider, httpClient);
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
                        _llmLog.LogCircuitBreaker(provider.Name, isOpen: true, 
                            $"Failure ratio exceeded threshold. Break duration: 5 minutes");
                        // Fire-and-forget database update (callback context doesn't support await)
                        _ = UpdateProviderHealthAsync(provider, isHealthy: false, "Circuit breaker opened", CancellationToken.None);
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = args =>
                    {
                        _logger.LogInformation("Circuit breaker closed for provider {Provider}", provider.Name);
                        _llmLog.LogCircuitBreaker(provider.Name, isOpen: false, "Provider recovered");
                        // Fire-and-forget database update (callback context doesn't support await)
                        _ = UpdateProviderHealthAsync(provider, isHealthy: true, null, CancellationToken.None);
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
                        _llmLog.LogRetry(provider.Name, provider.Model, args.AttemptNumber, 
                            args.Outcome.Exception?.Message);
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

    public async Task ResetProviderHealthAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await _providerRepo.GetByIdAsync(providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found");

        // Reset health status in database
        provider.IsHealthy = true;
        provider.LastError = null;
        provider.LastErrorAt = null;
        provider.UpdatedAt = DateTime.UtcNow;
        await _providerRepo.UpdateAsync(provider, ct);

        // Remove the cached circuit breaker so a fresh one is created on next use
        await _lock.WaitAsync(ct);
        try
        {
            _circuitBreakers.Remove(providerId);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation(
            "Reset health and circuit breaker for provider '{Name}' ({Id})",
            provider.Name, provider.Id);
    }

    /// <summary>
    /// Applies compact mode adjustments for small models.
    /// Reduces MaxTokens by 40% and sets temperature to 0.1 for determinism.
    /// </summary>
    private LlmRequestOptions ApplyCompactModeIfNeeded(LlmRequestOptions options, LlmProviderConfig provider)
    {
        // Determine if compact mode should be enabled
        var useCompactMode = options.CompactMode ?? IsSmallModel(provider.Model);
        
        if (!useCompactMode)
        {
            return options;
        }

        _logger.LogDebug("Applying compact mode for model {Model}", provider.Model);

        // Create new options with compact mode adjustments
        return new LlmRequestOptions
        {
            ProviderName = options.ProviderName,
            Temperature = 0.1f, // Low temperature for deterministic outputs
            MaxTokens = (int)(options.MaxTokens * 0.6), // Reduce by 40%
            UsageType = options.UsageType,
            WatchedSiteId = options.WatchedSiteId,
            ExpectJson = options.ExpectJson,
            CompactMode = true,
            PreferLargeModel = options.PreferLargeModel
        };
    }

    /// <summary>
    /// Detects if a model is considered "small" based on naming conventions.
    /// </summary>
    private static bool IsSmallModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return false;

        var lowerModel = modelName.ToLowerInvariant();
        
        // Check for named small-model indicators
        if (lowerModel.Contains("ministral") ||
            lowerModel.Contains("small") ||
            lowerModel.Contains("mini") ||
            lowerModel.Contains("tiny") ||
            lowerModel.Contains("nano") ||
            lowerModel.Contains("lite"))
            return true;

        // Check for parameter count indicators (e.g., ":3b", "-7b", ":8b")
        // Use boundary-aware matching to avoid "3b" matching "33b" or "13b"
        return ContainsSmallParamSize(lowerModel, "3b") ||
               ContainsSmallParamSize(lowerModel, "7b") ||
               ContainsSmallParamSize(lowerModel, "8b");
    }

    /// <summary>
    /// Checks if the model name contains a parameter size suffix (e.g. "3b") 
    /// that isn't preceded by another digit (to avoid "33b" matching "3b").
    /// </summary>
    private static bool ContainsSmallParamSize(string lowerModel, string sizeTag)
    {
        var index = lowerModel.IndexOf(sizeTag, StringComparison.Ordinal);
        while (index >= 0)
        {
            // Valid match if at start or preceded by a non-digit character
            if (index == 0 || !char.IsDigit(lowerModel[index - 1]))
                return true;
            index = lowerModel.IndexOf(sizeTag, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    public async Task<bool> HasLargeModelAsync(CancellationToken ct = default)
    {
        var providers = await _providerRepo.GetAllAsync(ct);
        return providers.Any(p => p.IsEnabled && p.IsHealthy && !IsSmallModel(p.Model));
    }
}
