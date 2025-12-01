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
        group.MapPost("/process-input", ProcessInput)
            .WithName("ProcessInput")
            .Produces<ProcessInputResponse>();

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

        group.MapGet("/providers/health", GetProviderHealth)
            .WithName("GetProviderHealth")
            .Produces<List<ProviderHealthStatus>>();

        group.MapGet("/usage", GetUsageStats)
            .WithName("GetUsageStats")
            .Produces<LlmUsageStatsDto>();

        return group;
    }

    private static async Task<IResult> ProcessInput(
        ProcessInputRequest request,
        IInputProcessor inputProcessor,
        CancellationToken ct)
    {
        // First analyze the input
        var analysis = inputProcessor.Analyze(request.Input);

        // If it's a URL, return immediately without LLM processing
        if (analysis.Type == InputType.Url)
        {
            return Results.Ok(new ProcessInputResponse
            {
                IsSuccess = true,
                Intent = "CreateWatch",
                ParsedRequest = new ParsedWatchRequestDto
                {
                    Url = analysis.NormalizedUrl
                }
            });
        }

        // Process with LLM
        var result = await inputProcessor.ProcessWithLlmAsync(request.Input, ct);

        return Results.Ok(new ProcessInputResponse
        {
            IsSuccess = result.IsSuccess,
            Intent = result.Intent.ToString(),
            ParsedRequest = result.ParsedRequest != null ? new ParsedWatchRequestDto
            {
                Url = result.ParsedRequest.Url,
                Title = result.ParsedRequest.Name,
                CssSelector = result.ParsedRequest.CssSelector,
                CheckIntervalMinutes = result.ParsedRequest.CheckInterval.HasValue 
                    ? (int)result.ParsedRequest.CheckInterval.Value.TotalMinutes 
                    : null,
                UseJavaScript = result.ParsedRequest.UseJavaScript,
                Tags = result.ParsedRequest.Tags,
                NotificationEmail = result.ParsedRequest.NotificationEmail,
                Description = result.ParsedRequest.Description
            } : null,
            NeedsClarification = result.NeedsClarification,
            ClarificationQuestions = result.ClarificationQuestions,
            Suggestions = result.Suggestions.Select(s => new SuggestionChipDto
            {
                Label = s.Label,
                Value = s.Value,
                Type = s.Type.ToString()
            }).ToList(),
            Summary = result.Summary,
            ErrorMessage = result.ErrorMessage,
            CreatedWatchId = result.CreatedWatchId?.ToString()
        });
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
