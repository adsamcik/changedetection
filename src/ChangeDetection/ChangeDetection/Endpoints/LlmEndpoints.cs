using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;
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

        group.MapPost("/run-pipeline", RunPipeline)
            .WithName("RunPipeline")
            .Produces<RunPipelineResponse>();

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

        group.MapGet("/pending-setups", GetPendingSetups)
            .WithName("GetPendingSetups")
            .Produces<List<PendingSetupDto>>();

        group.MapDelete("/sessions/{sessionId:guid}", DeleteSession)
            .WithName("DeleteSession")
            .Produces(204)
            .Produces(404);

        return group;
    }

    /// <summary>
    /// Deletes/cancels a setup session.
    /// </summary>
    private static IResult DeleteSession(Guid sessionId, IConversationSessionManager sessionManager)
    {
        var session = sessionManager.GetSession(sessionId);
        if (session is null)
        {
            return Results.NotFound();
        }

        sessionManager.RemoveSession(sessionId);
        return Results.NoContent();
    }

    /// <summary>
    /// Gets all setup sessions that are awaiting user input.
    /// </summary>
    private static IResult GetPendingSetups(IConversationSessionManager sessionManager)
    {
        var pending = sessionManager.GetSessionsAwaitingInput()
            .Select(s => new PendingSetupDto
            {
                SessionId = s.SessionId,
                DisplayName = s.DisplayName ?? "New Watch",
                CurrentPrompt = s.CurrentPrompt,
                LastActivityAt = s.LastActivityAt,
                CreatedAt = s.CreatedAt
            })
            .ToList();

        return Results.Ok(pending);
    }

    private static async Task<IResult> ProcessInput(
        ProcessInputRequest request,
        IInputProcessor inputProcessor,
        IWatchService watchService,
        IWatchSetupPipeline pipeline,
        ILlmProviderChain llmProvider,
        CancellationToken ct)
    {
        // Validate input
        if (string.IsNullOrWhiteSpace(request.Input))
        {
            return Results.BadRequest(new ProcessInputResponse
            {
                IsSuccess = false,
                ErrorMessage = "Input cannot be empty. Please provide a URL or describe what you want to monitor."
            });
        }

        // Check LLM availability early to fast-fail
        var providers = await llmProvider.GetAvailableProvidersAsync(ct);
        var llmAvailable = providers.Any();

        // First analyze the input
        var analysis = inputProcessor.Analyze(request.Input);

        // If it's a pure URL only (no additional text), create the watch immediately
        if (analysis.Type == InputType.Url)
        {
            var createRequest = new CreateWatchRequest
            {
                Url = analysis.NormalizedUrl!
            };

            var watch = await watchService.CreateWatchAsync(createRequest, ct);

            return Results.Ok(new ProcessInputResponse
            {
                IsSuccess = true,
                Intent = "CreateWatch",
                ParsedRequest = new ParsedWatchRequestDto
                {
                    Url = analysis.NormalizedUrl
                },
                CreatedWatchId = watch.Id.ToString(),
                Summary = $"Created watch for '{watch.Name ?? watch.Url}' - checking every {watch.CheckInterval.TotalMinutes} minutes."
            });
        }

        // If there's a URL plus natural language intent, run the pipeline to analyze content
        // and generate appropriate selectors based on the user's intent
        // Note: We always run the pipeline when URL + NL is detected (LLM-only, no heuristic keyword matching)
        if (analysis.Type == InputType.NaturalLanguage && !string.IsNullOrEmpty(analysis.DetectedUrl))
        {
            // Fast-fail if LLM is unavailable - don't silently fall back
            if (!llmAvailable)
            {
                return Results.Ok(new ProcessInputResponse
                {
                    IsSuccess = false,
                    ErrorMessage = "LLM service is currently unavailable. Please try again later or provide just the URL to create a basic watch.",
                    NeedsClarification = true,
                    Suggestions =
                    [
                        new SuggestionChipDto
                        {
                            Label = $"Create basic watch for {analysis.DetectedUrl}",
                            Value = analysis.NormalizedUrl ?? analysis.DetectedUrl!,
                            Type = "SetValue"
                        }
                    ]
                });
            }

            // Always run pipeline for URL + natural language (no heuristic keyword filtering)
            {
                // Run the full pipeline to analyze content and generate selectors
                var pipelineOptions = new PipelineOptions
                {
                    MaxIterations = 3,
                    MinConfidence = 0.6f
                };

                var pipelineResult = await pipeline.ProcessAsync(request.Input, pipelineOptions, ct);

                if (pipelineResult.IsSuccess && pipelineResult.FinalConfiguration != null)
                {
                    // Create watch with the pipeline-generated configuration
                    var createRequest = new CreateWatchRequest
                    {
                        Url = pipelineResult.FinalConfiguration.Url,
                        Name = pipelineResult.FinalConfiguration.Name,
                        CssSelector = pipelineResult.FinalConfiguration.CssSelector,
                        XPathSelector = pipelineResult.FinalConfiguration.XPathSelector,
                        UseJavaScript = pipelineResult.FinalConfiguration.UseJavaScript,
                        CheckInterval = pipelineResult.FinalConfiguration.CheckInterval,
                        Tags = pipelineResult.FinalConfiguration.Tags,
                        Description = pipelineResult.FinalConfiguration.Description
                    };

                    var watch = await watchService.CreateWatchAsync(createRequest, ct);

                    var selectorInfo = !string.IsNullOrEmpty(pipelineResult.FinalConfiguration.CssSelector)
                        ? $" targeting '{pipelineResult.FinalConfiguration.CssSelector}'"
                        : !string.IsNullOrEmpty(pipelineResult.FinalConfiguration.XPathSelector)
                            ? $" using XPath selector"
                            : "";

                    return Results.Ok(new ProcessInputResponse
                    {
                        IsSuccess = true,
                        Intent = "CreateWatch",
                        ParsedRequest = new ParsedWatchRequestDto
                        {
                            Url = pipelineResult.FinalConfiguration.Url,
                            Title = pipelineResult.FinalConfiguration.Name,
                            CssSelector = pipelineResult.FinalConfiguration.CssSelector,
                            Description = pipelineResult.FinalConfiguration.Description,
                            UseJavaScript = pipelineResult.FinalConfiguration.UseJavaScript,
                            CheckIntervalMinutes = pipelineResult.FinalConfiguration.CheckInterval.HasValue
                                ? (int)pipelineResult.FinalConfiguration.CheckInterval.Value.TotalMinutes
                                : null,
                            Tags = pipelineResult.FinalConfiguration.Tags
                        },
                        CreatedWatchId = watch.Id.ToString(),
                        Summary = $"Created watch for '{watch.Name ?? watch.Url}'{selectorInfo} - checking every {watch.CheckInterval.TotalMinutes} minutes."
                    });
                }

                // Pipeline failed - inform user and offer explicit choices instead of silent fallback
                var normalizedUrl = analysis.NormalizedUrl ?? analysis.DetectedUrl;
                var pipelineError = pipelineResult.ErrorMessage ?? "Unable to analyze page content";

                return Results.Ok(new ProcessInputResponse
                {
                    IsSuccess = false,
                    Intent = "CreateWatch",
                    ErrorMessage = $"Content analysis failed: {pipelineError}",
                    NeedsClarification = true,
                    ClarificationQuestions = [$"Would you like to create a basic watch for {normalizedUrl} that monitors the entire page?"],
                    ParsedRequest = new ParsedWatchRequestDto
                    {
                        Url = normalizedUrl,
                        Description = request.Input
                    },
                    Suggestions =
                    [
                        new SuggestionChipDto
                        {
                            Label = "Create basic watch (full page)",
                            Value = normalizedUrl!,
                            Type = "SetValue"
                        },
                        new SuggestionChipDto
                        {
                            Label = "Try again",
                            Value = request.Input,
                            Type = "SetValue"
                        }
                    ],
                    Summary = "I couldn't analyze the page content. You can create a basic watch that monitors the entire page, or try again."
                });
            }
        }

        // Process with LLM for other cases
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

    private static async Task<IResult> RunPipeline(
        RunPipelineRequest request,
        IWatchSetupPipeline pipeline,
        CancellationToken ct)
    {
        var options = new PipelineOptions
        {
            MaxIterations = request.MaxIterations ?? 3,
            MinConfidence = (float)(request.ConfidenceThreshold ?? 0.7)
        };

        var result = await pipeline.ProcessAsync(request.Input, options, ct);

        return Results.Ok(new RunPipelineResponse
        {
            IsSuccess = result.IsSuccess,
            Stage = result.CurrentStage.ToString(),
            IterationCount = result.Session.CurrentIteration,
            ExtractedUrls = result.Session.ExtractedUrls.Select(u => u.Url).ToList(),
            BestSelector = result.Session.BestSelector != null 
                ? MapGeneratedSelectorToDto(result.Session.BestSelector, result.Session.ValidationResults) 
                : null,
            AllSelectors = result.Session.GeneratedSelectors
                .Select(s => MapGeneratedSelectorToDto(s, result.Session.ValidationResults))
                .ToList(),
            ContentAnalysis = result.Session.ContentAnalysis != null ? new ContentAnalysisDto
            {
                PageType = result.Session.ContentAnalysis.ContentType.ToString(),
                UserIntent = result.Session.ContentAnalysis.UserIntent,
                ContentSections = result.Session.ContentAnalysis.IdentifiedSections.Select(s => new ContentSectionDto
                {
                    Name = s.Name,
                    Description = s.Description,
                    SuggestedSelector = s.SuggestedSelector,
                    Relevance = s.IsLikelyTarget ? 1.0 : 0.5
                }).ToList(),
                RecommendedApproach = result.Session.ContentAnalysis.RecommendedApproach.ToString()
            } : null,
            WatchConfig = result.FinalConfiguration != null ? new ParsedWatchRequestDto
            {
                Url = result.FinalConfiguration.Url,
                Title = result.FinalConfiguration.Name,
                CssSelector = result.FinalConfiguration.CssSelector,
                Description = result.FinalConfiguration.Description,
                UseJavaScript = result.FinalConfiguration.UseJavaScript
            } : null,
            ErrorMessage = result.ErrorMessage,
            Logs = result.Session.IterationHistory
        });
    }

    private static SelectorCandidateDto MapGeneratedSelectorToDto(
        GeneratedSelector selector,
        List<SelectorValidation> validations)
    {
        var validation = validations.FirstOrDefault(v => v.Selector.Selector == selector.Selector);
        return new SelectorCandidateDto
        {
            Type = selector.Type.ToString(),
            Expression = selector.Selector,
            Confidence = selector.Confidence,
            IsValidated = validation != null,
            MatchCount = validation?.MatchCount ?? 0,
            SampleText = validation?.ExtractedSample,
            Reasoning = selector.Reasoning
        };
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
