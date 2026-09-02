using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for LLM processing.
/// </summary>
public static class LlmEndpoints
{
    public static RouteGroupBuilder MapLlmEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/providers", GetProviders)
            .WithName("GetProviders")
            .Produces<List<LlmProviderDto>>();

        group.MapPost("/providers", CreateProvider)
            .WithName("CreateProvider")
            .Produces<LlmProviderDto>(201);

        group.MapPut("/providers/{id}", UpdateProvider)
            .WithName("UpdateProvider")
            .Produces<LlmProviderDto>();

        group.MapDelete("/providers/{id}", DeleteProvider)
            .WithName("DeleteProvider")
            .Produces(204)
            .Produces(404);

        group.MapPost("/providers/{id}/enable", EnableProvider)
            .WithName("EnableProvider")
            .Produces(204);

        group.MapPost("/providers/{id}/disable", DisableProvider)
            .WithName("DisableProvider")
            .Produces(204);

        group.MapPost("/providers/{id}/reset-health", ResetProviderHealth)
            .WithName("ResetProviderHealth")
            .Produces(204)
            .Produces(404);

        group.MapGet("/providers/health", GetProviderHealth)
            .WithName("GetProviderHealth")
            .Produces<List<ProviderHealthStatus>>();

        group.MapGet("/usage", GetUsageStats)
            .WithName("GetUsageStats")
            .Produces<LlmUsageStatsDto>();

        group.MapGet("/logs", GetLogs)
            .WithName("GetLlmLogs")
            .Produces<LlmLogsResponse>();

        group.MapDelete("/logs", ClearLogs)
            .WithName("ClearLlmLogs")
            .Produces(204);

        group.MapPost("/copilot-test", CopilotTest)
            .WithName("CopilotTest")
            .WithDescription("Direct Copilot SDK test — bypasses pipeline and circuit breaker");

        return group;
    }

    /// <summary>
    /// Direct test of the Copilot SDK — sends a simple prompt and waits for response.
    /// Bypasses the LLM provider chain, circuit breaker, and pipeline.
    /// </summary>
    private static async Task<IResult> CopilotTest(
        ILlmProviderChain llmProvider,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("CopilotTest");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation("CopilotTest: Starting direct SDK test");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            
            var response = await llmProvider.ExecuteAsync(
                "Reply with exactly: COPILOT_SDK_OK",
                new LlmRequestOptions { MaxTokens = 50, Temperature = 0 },
                cts.Token);

            sw.Stop();
            return Results.Ok(new
            {
                success = response.IsSuccess,
                content = response.Content,
                provider = response.ProviderUsed,
                model = response.Model,
                durationMs = sw.ElapsedMilliseconds,
                error = response.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "CopilotTest: Failed after {Duration}ms", sw.ElapsedMilliseconds);
            return Results.Ok(new
            {
                success = false,
                content = (string?)null,
                provider = (string?)null,
                model = (string?)null,
                durationMs = sw.ElapsedMilliseconds,
                error = $"{ex.GetType().Name}: {ex.Message}"
            });
        }
    }

    private static async Task<IResult> GetProviders(
        IRepository<LlmProviderConfig> providerRepo,
        CancellationToken ct)
    {
        var providers = await providerRepo.GetAllAsync(ct);
        var dtos = providers.OrderBy(p => p.Priority).Select(MapToDto).ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> CreateProvider(
        LlmProviderCreateDto dto,
        IRepository<LlmProviderConfig> providerRepo,
        CancellationToken ct)
    {
        var provider = new LlmProviderConfig
        {
            Name = dto.ProviderType,
            ProviderType = Enum.Parse<LlmProviderType>(dto.ProviderType),
            Priority = dto.Priority,
            IsEnabled = dto.IsEnabled,
            Endpoint = dto.Endpoint,
            Model = dto.ModelId ?? "",
            ApiKey = dto.ApiKey,
            TimeoutSeconds = 60, // Default timeout
            MaxRetries = 3, // Default retries
            CostPer1KInputTokens = dto.CostPerInputToken * 1000m,
            CostPer1KOutputTokens = dto.CostPerOutputToken * 1000m
        };

        await providerRepo.InsertAsync(provider, ct);

        return Results.Created($"/api/llm/providers/{provider.Id}", MapToDto(provider));
    }

    private static async Task<IResult> UpdateProvider(
        string id,
        LlmProviderCreateDto dto,
        IRepository<LlmProviderConfig> providerRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid provider ID");

        var provider = await providerRepo.GetByIdAsync(guidId, ct);
        if (provider == null)
            return Results.NotFound();

        if (!string.IsNullOrEmpty(dto.ProviderType))
            provider.ProviderType = Enum.Parse<LlmProviderType>(dto.ProviderType);
        
        provider.Priority = dto.Priority > 0 ? dto.Priority : provider.Priority;
        provider.IsEnabled = dto.IsEnabled;
        
        if (!string.IsNullOrEmpty(dto.Endpoint))
            provider.Endpoint = dto.Endpoint;
        
        if (!string.IsNullOrEmpty(dto.ModelId))
            provider.Model = dto.ModelId;
        
        if (!string.IsNullOrEmpty(dto.ApiKey))
            provider.ApiKey = dto.ApiKey;
        
        provider.CostPer1KInputTokens = dto.CostPerInputToken * 1000m;
        provider.CostPer1KOutputTokens = dto.CostPerOutputToken * 1000m;
        provider.UpdatedAt = DateTime.UtcNow;

        await providerRepo.UpdateAsync(provider, ct);

        return Results.Ok(MapToDto(provider));
    }

    private static async Task<IResult> EnableProvider(
        string id,
        IRepository<LlmProviderConfig> providerRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid provider ID");

        var provider = await providerRepo.GetByIdAsync(guidId, ct);
        if (provider == null) return Results.NotFound();

        provider.IsEnabled = true;
        provider.UpdatedAt = DateTime.UtcNow;
        await providerRepo.UpdateAsync(provider, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> DisableProvider(
        string id,
        IRepository<LlmProviderConfig> providerRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid provider ID");

        var provider = await providerRepo.GetByIdAsync(guidId, ct);
        if (provider == null) return Results.NotFound();

        provider.IsEnabled = false;
        provider.UpdatedAt = DateTime.UtcNow;
        await providerRepo.UpdateAsync(provider, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ResetProviderHealth(
        string id,
        ILlmProviderChain llmChain,
        IRepository<LlmProviderConfig> providerRepo,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid provider ID");

        var provider = await providerRepo.GetByIdAsync(guidId, ct);
        if (provider == null) return Results.NotFound();

        await llmChain.ResetProviderHealthAsync(guidId, ct);

        var logger = loggerFactory.CreateLogger(typeof(LlmEndpoints).FullName!);
        logger.LogInformation("Provider '{Name}' health reset via API", provider.Name);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteProvider(
        string id,
        IRepository<LlmProviderConfig> providerRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid provider ID");

        var provider = await providerRepo.GetByIdAsync(guidId, ct);
        if (provider == null)
            return Results.NotFound();

        await providerRepo.DeleteAsync(guidId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetProviderHealth(
        ILlmProviderChain llmChain,
        CancellationToken ct)
    {
        var health = await llmChain.GetHealthStatusAsync(ct);
        return Results.Ok(health.ToList());
    }

    private static async Task<IResult> GetUsageStats(
        IRepository<LlmUsageRecord> usageRepo,
        CancellationToken ct)
    {
        var allUsage = await usageRepo.GetAllAsync(ct);
        
        var byProvider = allUsage
            .GroupBy(u => u.ProviderName)
            .ToDictionary(
                g => g.Key,
                g => new ProviderUsageDto
                {
                    RequestCount = g.Count(),
                    InputTokens = g.Sum(u => u.InputTokens),
                    OutputTokens = g.Sum(u => u.OutputTokens),
                    Cost = g.Sum(u => u.Cost)
                });

        var stats = new LlmUsageStatsDto
        {
            TotalRequests = allUsage.Count(),
            SuccessCount = allUsage.Count(u => u.IsSuccess),
            FailureCount = allUsage.Count(u => !u.IsSuccess),
            TotalInputTokens = allUsage.Sum(u => u.InputTokens),
            TotalOutputTokens = allUsage.Sum(u => u.OutputTokens),
            TotalCost = allUsage.Sum(u => u.Cost),
            AverageLatencyMs = allUsage.Any() ? allUsage.Average(u => u.DurationMs) : 0,
            ByProvider = byProvider
        };

        return Results.Ok(stats);
    }

    /// <summary>
    /// Gets recent LLM logs for debugging.
    /// </summary>
    private static IResult GetLogs(
        ILlmLogService logService,
        IWebHostEnvironment env,
        int count = 100,
        string? provider = null)
    {
        var logs = provider != null 
            ? logService.GetLogsForProvider(provider, count)
            : logService.GetRecentLogs(count);

        var isDevelopment = env.IsDevelopment();

        var dtos = logs.Select(log => new LlmLogEntryDto
        {
            Id = log.Id,
            RequestId = log.RequestId,
            Timestamp = log.Timestamp,
            Level = log.Level.ToString(),
            ProviderName = log.ProviderName,
            Model = log.Model,
            Category = log.Category.ToString(),
            Message = log.Message,
            PromptPreview = log.PromptPreview,
            // In production, truncate full content to prevent exposure of sensitive data
            FullPrompt = isDevelopment ? log.FullPrompt : TruncateForProduction(log.FullPrompt),
            ResponsePreview = log.ResponsePreview,
            FullResponse = isDevelopment ? log.FullResponse : TruncateForProduction(log.FullResponse),
            ErrorMessage = log.ErrorMessage,
            // Sanitize exception type to just the type name (no assembly info)
            ExceptionType = SanitizeExceptionType(log.ExceptionType),
            // Only include stack trace in development mode - exposes internal file paths and method signatures
            StackTrace = isDevelopment ? log.StackTrace : null,
            DurationMs = log.DurationMs,
            InputTokens = log.InputTokens,
            OutputTokens = log.OutputTokens,
            IsSuccess = log.IsSuccess,
            Metadata = isDevelopment ? log.Metadata : null
        }).ToList();

        return Results.Ok(new LlmLogsResponse
        {
            Logs = dtos,
            TotalCount = dtos.Count
        });
    }

    /// <summary>
    /// Truncates content for production to a safe preview length.
    /// </summary>
    private static string? TruncateForProduction(string? content, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        if (content.Length <= maxLength)
            return content;

        return content[..maxLength] + "... [truncated in production]";
    }

    /// <summary>
    /// Sanitizes exception type to just the type name without assembly info.
    /// </summary>
    private static string? SanitizeExceptionType(string? exceptionType)
    {
        if (string.IsNullOrEmpty(exceptionType))
            return exceptionType;

        // Extract just the type name from fully qualified names like
        // "System.Net.Http.HttpRequestException, System.Net.Http, Version=..."
        var commaIndex = exceptionType.IndexOf(',');
        if (commaIndex > 0)
        {
            exceptionType = exceptionType[..commaIndex];
        }

        // Get just the class name without namespace for cleaner display
        // "System.Net.Http.HttpRequestException" -> "HttpRequestException"
        var lastDotIndex = exceptionType.LastIndexOf('.');
        return lastDotIndex > 0 ? exceptionType[(lastDotIndex + 1)..] : exceptionType;
    }

    /// <summary>
    /// Clears all LLM logs.
    /// </summary>
    private static IResult ClearLogs(ILlmLogService logService)
    {
        logService.Clear();
        return Results.NoContent();
    }

    private static LlmProviderDto MapToDto(LlmProviderConfig p) => new()
    {
        Id = p.Id.ToString(),
        ProviderType = p.ProviderType.ToString(),
        ModelId = p.Model,
        Endpoint = p.Endpoint,
        Priority = p.Priority,
        MaxTokens = 4096, // Default value since entity doesn't track this
        CostPerInputToken = p.CostPer1KInputTokens / 1000m,
        CostPerOutputToken = p.CostPer1KOutputTokens / 1000m,
        IsEnabled = p.IsEnabled,
        IsHealthy = p.IsHealthy,
        LastError = p.LastError,
        LastUsed = p.LastErrorAt // Using LastErrorAt as an approximation
    };
}
