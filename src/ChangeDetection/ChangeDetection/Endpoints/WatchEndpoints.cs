using ChangeDetection.Core;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services;
using ChangeDetection.Services.Background;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Shared.Dtos;
using Microsoft.AspNetCore.OutputCaching;
using System.Text.Json;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for watch management.
/// </summary>
public static class WatchEndpoints
{
    public static RouteGroupBuilder MapWatchEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAllWatches)
            .WithName("GetAllWatches")
            .Produces<List<WatchListItemDto>>()
            .CacheOutput(policy => policy.Expire(TimeSpan.FromSeconds(5)).Tag("watches").SetVaryByHeader("Remote-User"));

        group.MapGet("/{id}", GetWatchById)
            .WithName("GetWatchById")
            .Produces<WatchDetailDto>()
            .Produces(404)
            .CacheOutput(policy => policy.Expire(TimeSpan.FromSeconds(10)).Tag("watches").SetVaryByRouteValue("id").SetVaryByHeader("Remote-User"));

        group.MapPost("/", CreateWatch)
            .WithName("CreateWatch")
            .Produces<WatchDetailDto>(201);

        group.MapPut("/{id}", UpdateWatch)
            .WithName("UpdateWatch")
            .Produces<WatchDetailDto>()
            .Produces(404);

        group.MapGet("/{id}/pipeline", GetPipelineDefinition)
            .WithName("GetPipelineDefinition")
            .Produces<WatchPipelineDto>()
            .Produces(404);

        group.MapDelete("/{id}", DeleteWatch)
            .WithName("DeleteWatch")
            .Produces(204)
            .Produces(404);

        group.MapPost("/{id}/check", TriggerCheck)
            .WithName("TriggerCheck")
            .Produces<ChangeListItemDto?>()
            .Produces(404);

        group.MapPost("/{id}/enable", EnableWatch)
            .WithName("EnableWatch")
            .Produces(204)
            .Produces(404);

        group.MapPost("/{id}/disable", DisableWatch)
            .WithName("DisableWatch")
            .Produces(204)
            .Produces(404);

        group.MapGet("/{id}/price-history", GetPriceHistory)
            .WithName("GetPriceHistory")
            .Produces<PriceHistoryResponseDto>()
            .Produces(404);

        group.MapGet("/{id}/thresholds/{fieldName}", GetFieldThresholds)
            .WithName("GetFieldThresholds")
            .Produces<List<AlertThresholdDto>>()
            .Produces(404);

        group.MapPost("/{id}/thresholds/{fieldName}", AddFieldThreshold)
            .WithName("AddFieldThreshold")
            .Produces<AlertThresholdDto>(201)
            .Produces(404);

        group.MapPut("/{id}/thresholds/{fieldName}/{thresholdId}", UpdateFieldThreshold)
            .WithName("UpdateFieldThreshold")
            .Produces<AlertThresholdDto>()
            .Produces(404);

        group.MapDelete("/{id}/thresholds/{fieldName}/{thresholdId}", DeleteFieldThreshold)
            .WithName("DeleteFieldThreshold")
            .Produces(204)
            .Produces(404);

        // Bulk operations
        group.MapPost("/bulk/enable", BulkEnable)
            .WithName("BulkEnableWatches")
            .Produces<BulkOperationResultDto>();

        group.MapPost("/bulk/disable", BulkDisable)
            .WithName("BulkDisableWatches")
            .Produces<BulkOperationResultDto>();

        group.MapPost("/bulk/delete", BulkDelete)
            .WithName("BulkDeleteWatches")
            .Produces<BulkOperationResultDto>();

        group.MapPost("/bulk/check", BulkCheck)
            .WithName("BulkCheckWatches")
            .Produces<BulkOperationResultDto>();

        group.MapPost("/bulk/edit", BulkEdit)
            .WithName("BulkEditWatches")
            .Produces<BulkOperationResultDto>();

        // Dev endpoints for pipeline testing
        group.MapPut("/{id}/pipeline", SetPipelineDefinition)
            .WithName("SetPipelineDefinition")
            .Produces(204)
            .Produces(404);

        group.MapPost("/{id}/pipeline/execute", ExecutePipeline)
            .WithName("ExecutePipeline")
            .Produces<object>()
            .Produces(404);

        group.MapGet("/{id}/pipeline/status", GetPipelineStatus)
            .WithName("GetPipelineStatus")
            .Produces<object>()
            .Produces(404);

        // Search discovery: promote a search result to a standalone watch
        group.MapPost("/{id}/promote", PromoteSearchResult)
            .WithName("PromoteSearchResult")
            .Produces<WatchDetailDto>(201)
            .Produces(404)
            .Produces(400);

        return group;
    }

    private static async Task<IResult> GetAllWatches(
        IWatchService watchService,
        IRepository<ChangeEvent> eventRepo,
        IRepository<ChangeSnapshot> snapshotRepo,
        ICategoryService categoryService,
        IPipelineRunSummaryStore summaryStore,
        CancellationToken ct)
    {
        var watches = await watchService.GetAllAsync(ct);
        var categories = (await categoryService.GetAllAsync(ct)).ToDictionary(c => c.Id);

        // Batch-load pipeline run summaries for all watches
        var watchIds = watches.Select(w => w.Id.ToString()).ToList();
        var summaries = await summaryStore.GetBatchAsync(watchIds, ct);

        var dtos = new List<WatchListItemDto>();

        foreach (var watch in watches)
        {
            var changeCount = await eventRepo.CountAsync(e => e.WatchedSiteId == watch.Id, ct);
            var unviewedChanges = (await eventRepo.FindAsync(e => e.WatchedSiteId == watch.Id && !e.IsViewed, ct)).ToList();
            var latestChange = (await eventRepo.FindAsync(e => e.WatchedSiteId == watch.Id, ct))
                .OrderByDescending(e => e.DetectedAt)
                .FirstOrDefault();
            
            // Get latest snapshot item count
            var latestSnapshot = (await snapshotRepo.FindAsync(s => s.WatchedSiteId == watch.Id, ct))
                .OrderByDescending(s => s.CapturedAt)
                .FirstOrDefault();
            
            // Get category info
            Category? category = null;
            if (watch.CategoryId.HasValue && categories.TryGetValue(watch.CategoryId.Value, out var cat))
                category = cat;

            // Determine extraction quality from pipeline summary
            var itemCount = CountSnapshotItems(latestSnapshot);
            summaries.TryGetValue(watch.Id.ToString(), out var runSummary);
            var quality = DetermineExtractionQuality(watch, itemCount, runSummary);

            dtos.Add(new WatchListItemDto
            {
                Id = watch.Id.ToString(),
                Url = watch.Url,
                Title = watch.Name,
                CssSelector = watch.CssSelector,
                CheckInterval = watch.CheckInterval,
                LastCheck = watch.LastChecked,
                Status = watch.Status.ToString(),
                IsEnabled = watch.IsEnabled,
                ChangeCount = changeCount,
                HasRecentChanges = unviewedChanges.Count > 0,
                LastError = watch.LastError,
                LatestChangeId = latestChange?.Id.ToString(),
                LatestChangeSummary = latestChange?.DiffSummary,
                LatestChangeAt = latestChange?.DetectedAt,
                UnviewedChangeCount = unviewedChanges.Count,
                LatestItemCount = itemCount,
                CategoryId = category?.Id.ToString(),
                CategoryName = category?.Name,
                CategoryColor = category?.Color,
                ExtractionQuality = quality,
                HealthStatus = WatchHealthClassifier.Classify(watch, runSummary).ToString(),
                NeedsPipelineSetup = watch.NeedsPipelineSetup,
                Tags = watch.Tags.Select(t => new TagDto
                {
                    Name = t,
                    Color = TagColorGenerator.GetColor(t, watch.TagColors),
                    IsColorOverridden = watch.TagColors.ContainsKey(t)
                }).ToList()
            });
        }

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetWatchById(
        string id,
        IWatchService watchService,
        IRepository<ChangeSnapshot> snapshotRepo,
        ICategoryService categoryService,
        IPipelineRunSummaryStore summaryStore,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch == null)
            return Results.NotFound();

        // Get latest snapshot
        var snapshots = await snapshotRepo.FindAsync(s => s.WatchedSiteId == watch.Id, ct);
        var latestSnapshot = snapshots.OrderByDescending(s => s.CapturedAt).FirstOrDefault();
        
        // Get category info
        Category? category = null;
        if (watch.CategoryId.HasValue)
            category = await categoryService.GetByIdAsync(watch.CategoryId.Value, ct);

        var dto = MapToDetailDto(watch, latestSnapshot, category);

        // Attach pipeline health from latest run summary
        var runSummary = await summaryStore.GetAsync(guidId.ToString(), ct);
        if (runSummary is not null)
            dto.PipelineHealth = MapToPipelineHealthDto(runSummary);

        // Attach outreach assessment if available
        var outreachAssessment = OutreachSignalDetector.Deserialize(watch.OutreachAssessmentJson);
        if (outreachAssessment is { IsOutreachFriendly: true })
        {
            dto.Outreach = new GroupOutreachDto
            {
                IsOutreachFriendly = true,
                OverallScore = outreachAssessment.OverallScore,
                Signals = outreachAssessment.Signals.Select(s => new OutreachSignalDto
                {
                    Type = s.Type,
                    Evidence = s.Evidence,
                    Confidence = s.Confidence
                }).ToList()
            };
        }

        return Results.Ok(dto);
    }

    private static async Task<IResult> CreateWatch(
        WatchCreateDto dto,
        IWatchService watchService,
        ICategoryService categoryService,
        IUrlValidator urlValidator,
        CancellationToken ct)
    {
        var urlValidationError = urlValidator.Validate(dto.Url);
        if (urlValidationError is not null)
            return Results.BadRequest(urlValidationError);

        var request = new CreateWatchRequest
        {
            Url = dto.Url,
            Name = dto.Title,
            CssSelector = dto.CssSelector,
            XPathSelector = dto.XpathSelector,
            CheckInterval = dto.CheckInterval,
            Tags = TagNormalizer.NormalizeList(dto.Tags),
            IgnorePatterns = dto.IgnorePatterns,
            TagColors = dto.TagColors,
            CategoryId = dto.CategoryId != null ? Guid.Parse(dto.CategoryId) : null,
            ScheduleSettings = MapScheduleSettingsFromDto(dto.ScheduleSettings),
            SourceType = (SourceType)(int)dto.SourceType,
            SearchConfig = dto.SearchConfig is not null ? new SearchConfig
            {
                Query = dto.SearchConfig.Query,
                ProviderId = dto.SearchConfig.ProviderId,
                Category = dto.SearchConfig.Category,
                Language = dto.SearchConfig.Language,
                TimeRange = dto.SearchConfig.TimeRange,
                MaxResults = dto.SearchConfig.MaxResults,
                AutoPromotionRules = dto.SearchConfig.AutoPromotionRules.Select(r => new AutoPromotionRule
                {
                    UrlPattern = r.UrlPattern,
                    TitleContains = r.TitleContains,
                    IsEnabled = r.IsEnabled,
                    CssSelector = r.CssSelector
                }).ToList()
            } : null
        };

        if (dto.FetchSettings != null)
        {
            request.UseJavaScript = dto.FetchSettings.UseJavaScript;
            request.FetchSettings = new FetchSettings
            {
                UseJavaScript = dto.FetchSettings.UseJavaScript,
                WaitForSelector = dto.FetchSettings.WaitForSelector,
                WaitAfterLoadMs = dto.FetchSettings.WaitTimeMs,
                TimeoutSeconds = dto.FetchSettings.TimeoutSeconds,
                Headers = dto.FetchSettings.CustomHeaders ?? new(),
                ViewportWidth = dto.FetchSettings.ViewportWidth,
                ViewportHeight = dto.FetchSettings.ViewportHeight,
                ProxyUrl = dto.FetchSettings.ProxyUrl,
                UserAgent = dto.FetchSettings.UserAgent,
                Screenshot = MapScreenshotSettingsFromDto(dto.FetchSettings.Screenshot)
            };
        }

        if (dto.NotificationSettings != null)
        {
            request.Notifications = new NotificationSettings
            {
                EmailEnabled = dto.NotificationSettings.EmailEnabled,
                EmailAddress = dto.NotificationSettings.EmailRecipients?.FirstOrDefault(),
                WebhookEnabled = dto.NotificationSettings.WebhookEnabled,
                WebhookUrl = dto.NotificationSettings.WebhookUrl,
                DiscordEnabled = dto.NotificationSettings.DiscordEnabled,
                DiscordWebhookUrl = dto.NotificationSettings.DiscordWebhookUrl,
                UseLlmSummary = dto.NotificationSettings.UseLlmSummary,
                DefaultChannelName = dto.NotificationSettings.DefaultChannelName,
                Channels = dto.NotificationSettings.Channels.Select(MapNotificationChannelFromDto).ToList(),
                MinimumImportance = Enum.TryParse<ChangeImportance>(dto.NotificationSettings.MinimumImportanceToNotify, out var imp) 
                    ? imp : ChangeImportance.Medium
            };
        }

        var watch = await watchService.CreateWatchAsync(request, ct);
        
        // Get category info for response
        Category? category = null;
        if (watch.CategoryId.HasValue)
            category = await categoryService.GetByIdAsync(watch.CategoryId.Value, ct);
        
        return Results.Created($"/api/watches/{watch.Id}", MapToDetailDto(watch, null, category));
    }

    private static async Task<IResult> UpdateWatch(
        string id,
        WatchCreateDto dto,
        IWatchService watchService,
        IRepository<ChangeSnapshot> snapshotRepo,
        ICategoryService categoryService,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch == null)
            return Results.NotFound();

        watch.Name = dto.Title;
        watch.CssSelector = dto.CssSelector;
        watch.XPathSelector = dto.XpathSelector;
        watch.Tags = TagNormalizer.NormalizeList(dto.Tags);
        watch.IgnorePatterns = dto.IgnorePatterns ?? [];
        watch.TagColors = dto.TagColors ?? [];
        watch.CategoryId = dto.CategoryId != null ? Guid.Parse(dto.CategoryId) : null;
        watch.CheckInterval = dto.CheckInterval;
        watch.IsEnabled = dto.IsEnabled;
        
        // Update schedule settings
        var scheduleSettings = MapScheduleSettingsFromDto(dto.ScheduleSettings);
        if (scheduleSettings != null)
        {
            watch.ScheduleSettings = scheduleSettings;
            // Sync base interval with check interval for fixed mode
            if (scheduleSettings.Mode == CheckScheduleMode.Fixed)
            {
                watch.ScheduleSettings.BaseInterval = dto.CheckInterval;
            }
        }

        if (dto.FetchSettings != null)
        {
            watch.FetchSettings.UseJavaScript = dto.FetchSettings.UseJavaScript;
            watch.FetchSettings.WaitForSelector = dto.FetchSettings.WaitForSelector;
            watch.FetchSettings.WaitAfterLoadMs = dto.FetchSettings.WaitTimeMs;
            watch.FetchSettings.TimeoutSeconds = dto.FetchSettings.TimeoutSeconds;
            watch.FetchSettings.ViewportWidth = dto.FetchSettings.ViewportWidth;
            watch.FetchSettings.ViewportHeight = dto.FetchSettings.ViewportHeight;
            watch.FetchSettings.ProxyUrl = dto.FetchSettings.ProxyUrl;
            watch.FetchSettings.UserAgent = dto.FetchSettings.UserAgent;
            watch.FetchSettings.Screenshot = MapScreenshotSettingsFromDto(dto.FetchSettings.Screenshot);
            if (dto.FetchSettings.CustomHeaders != null)
            {
                watch.FetchSettings.Headers = dto.FetchSettings.CustomHeaders;
            }
        }

        if (dto.NotificationSettings != null)
        {
            watch.Notifications = new NotificationSettings
            {
                EmailEnabled = dto.NotificationSettings.EmailEnabled,
                EmailAddress = dto.NotificationSettings.EmailRecipients?.FirstOrDefault(),
                WebhookEnabled = dto.NotificationSettings.WebhookEnabled,
                WebhookUrl = dto.NotificationSettings.WebhookUrl,
                DiscordEnabled = dto.NotificationSettings.DiscordEnabled,
                DiscordWebhookUrl = dto.NotificationSettings.DiscordWebhookUrl,
                UseLlmSummary = dto.NotificationSettings.UseLlmSummary,
                DefaultChannelName = dto.NotificationSettings.DefaultChannelName,
                Channels = dto.NotificationSettings.Channels.Select(MapNotificationChannelFromDto).ToList(),
                MinimumImportance = Enum.TryParse<ChangeImportance>(dto.NotificationSettings.MinimumImportanceToNotify, out var imp) 
                    ? imp : ChangeImportance.Medium
            };
        }

        await watchService.UpdateWatchAsync(watch, ct);

        var snapshots = await snapshotRepo.FindAsync(s => s.WatchedSiteId == watch.Id, ct);
        var latestSnapshot = snapshots.OrderByDescending(s => s.CapturedAt).FirstOrDefault();

        Category? category = null;
        if (watch.CategoryId.HasValue)
            category = await categoryService.GetByIdAsync(watch.CategoryId.Value, ct);

        return Results.Ok(MapToDetailDto(watch, latestSnapshot, category));
    }

    private static async Task<IResult> DeleteWatch(
        string id,
        IWatchService watchService,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch == null)
            return Results.NotFound();

        await watchService.DeleteWatchAsync(guidId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TriggerCheck(
        string id,
        IWatchService watchService,
        IPipelineExecutor pipelineExecutor,
        IBlockStateStore stateStore,
        IPipelineRunSummaryStore summaryStore,
        IWatchExecutionLock executionLock,
        SetupFlowEnhancements setupFlow,
        IRepository<WatchedSite> watchRepo,
        ILogger<Program> logger,
        IComposableSetupPipeline? composableSetup,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch == null)
            return Results.NotFound();

        if (!executionLock.TryAcquire(guidId))
            return Results.Conflict("Watch is already being checked.");

        try
        {

        // Auto-generate pipeline if missing (mirrors ChangeCheckBackgroundService logic)
        if (string.IsNullOrEmpty(watch.PipelineDefinitionJson))
        {
            PipelineDefinition? pipeline = null;

            // Step 1: Detect platform from URL and apply template
            var detectedPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(watch.Url);
            if (detectedPlatform is not null)
            {
                pipeline = await setupFlow.GetPlatformTemplateAsync(detectedPlatform, watch.Url, ct: ct);
                if (pipeline is not null)
                {
                    logger.LogInformation(
                        "TriggerCheck: auto-generated {Platform} platform pipeline for watch {WatchId}",
                        detectedPlatform, watch.Id);
                }
            }

            // Step 2: For group watches, attempt headless LLM pipeline building
            if (pipeline is null && watch.GroupId.HasValue && composableSetup is not null)
            {
                const int maxHeadlessBuildAttempts = 2;
                if (watch.HeadlessBuildAttempts < maxHeadlessBuildAttempts)
                {
                    try
                    {
                        logger.LogInformation(
                            "TriggerCheck: attempting headless LLM pipeline build for watch {WatchId} ({Url}), attempt {Attempt}",
                            watch.Id, watch.Url, watch.HeadlessBuildAttempts + 1);

                        pipeline = await composableSetup.BuildPipelineHeadlessAsync(
                            watch.Url, watch.UserIntent, ct);

                        if (pipeline is not null)
                        {
                            logger.LogInformation(
                                "TriggerCheck: headless LLM pipeline built successfully for watch {WatchId}: {BlockCount} blocks",
                                watch.Id, pipeline.Blocks.Count);
                            watch.NeedsPipelineSetup = false;
                            watch.HeadlessBuildAttempts = 0;
                            watch.LastError = null;
                        }
                        else
                        {
                            watch.HeadlessBuildAttempts++;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "TriggerCheck: headless LLM pipeline build failed for watch {WatchId}, attempt {Attempt}",
                            watch.Id, watch.HeadlessBuildAttempts + 1);
                        watch.HeadlessBuildAttempts++;
                    }
                }

                if (pipeline is null)
                {
                    watch.NeedsPipelineSetup = true;
                    watch.LastError = watch.HeadlessBuildAttempts >= maxHeadlessBuildAttempts
                        ? "Manual setup required — could not automatically identify listings on this page after multiple attempts"
                        : "Could not automatically identify job listings on this page";
                    watch.UpdatedAt = DateTime.UtcNow;
                    await watchRepo.UpdateAsync(watch, ct);
                    return Results.UnprocessableEntity(new { error = watch.LastError, needsSetup = true });
                }
            }

            // Step 3: Fall back to basic pipeline
            pipeline ??= ChangeCheckBackgroundService.GenerateBasicPipeline(watch.Url, watch.CssSelector);

            // Persist generated pipeline
            watch.PipelineDefinitionJson = PipelineSerializer.Serialize(pipeline);
            watch.UpdatedAt = DateTime.UtcNow;
            await watchRepo.UpdateAsync(watch, ct);
        }

        // Pipeline-aware check: use pipeline executor if pipeline is defined
        if (!string.IsNullOrEmpty(watch.PipelineDefinitionJson))
        {
            var definition = PipelineSerializer.Deserialize(watch.PipelineDefinitionJson, logger);
            if (definition is null)
                return Results.BadRequest("Invalid pipeline definition");

            var result = await pipelineExecutor.ExecuteAsync(definition, guidId, stateStore, null, ct);

            // Persist pipeline run summary for observability
            var summary = PipelineRunSummaryBuilder.Build(guidId.ToString(), result, definition);
            await summaryStore.SaveAsync(summary, ct);

            logger.LogInformation(
                "Pipeline check for watch {WatchId}: Success={Success}, Blocks={BlockCount}",
                guidId, result.Success, result.BlockResults.Count);

            if (!result.Success)
                return Results.Ok<ChangeListItemDto?>(null);

            // Return a summary of the pipeline execution
            var listDiffBlock = result.BlockResults
                .FirstOrDefault(kvp => kvp.Key.StartsWith("listdiff", StringComparison.OrdinalIgnoreCase));

            bool hasChanges = false;
            int objectsAdded = 0, objectsRemoved = 0;

            if (listDiffBlock.Value?.Output is { } diffOutput &&
                diffOutput.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (diffOutput.TryGetProperty("changed", out var changedProp))
                    hasChanges = changedProp.GetBoolean();
                if (diffOutput.TryGetProperty("added", out var addedProp) && addedProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    objectsAdded = addedProp.GetArrayLength();
                if (diffOutput.TryGetProperty("removed", out var removedProp) && removedProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    objectsRemoved = removedProp.GetArrayLength();
            }

            if (result.WasBaseline || !hasChanges)
                return Results.Ok<ChangeListItemDto?>(null);

            return Results.Ok(new ChangeListItemDto
            {
                Id = Guid.NewGuid().ToString(),
                WatchId = watch.Id.ToString(),
                WatchTitle = watch.Name,
                DetectedAt = DateTime.UtcNow,
                Summary = $"{objectsAdded} items added, {objectsRemoved} items removed.",
                Importance = (objectsAdded + objectsRemoved) > 0 ? "High" : "Medium",
                LinesAdded = objectsAdded,
                LinesRemoved = objectsRemoved,
                IsViewed = false,
                IsNotified = false
            });
        }

        // Legacy path for non-pipeline watches
        var changeEvent = await watchService.CheckForChangesAsync(guidId, ct);
        
        if (changeEvent == null)
            return Results.Ok<ChangeListItemDto?>(null);

        return Results.Ok(new ChangeListItemDto
        {
            Id = changeEvent.Id.ToString(),
            WatchId = watch.Id.ToString(),
            WatchTitle = watch.Name,
            DetectedAt = changeEvent.DetectedAt,
            Summary = changeEvent.DiffSummary ?? "Changes detected",
            Importance = changeEvent.Importance.ToString(),
            LinesAdded = changeEvent.LinesAdded,
            LinesRemoved = changeEvent.LinesRemoved,
            IsViewed = changeEvent.IsViewed,
            IsNotified = changeEvent.IsNotified
        });
        }
        finally
        {
            executionLock.Release(guidId);
        }
    }

    private static async Task<IResult> EnableWatch(
        string id,
        IWatchService watchService,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch == null)
            return Results.NotFound();

        await watchService.EnableWatchAsync(guidId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DisableWatch(
        string id,
        IWatchService watchService,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch == null)
            return Results.NotFound();

        await watchService.DisableWatchAsync(guidId, ct);
        return Results.NoContent();
    }

    // ============================================================================
    // Bulk Operations
    // ============================================================================

    private static async Task<IResult> BulkEnable(
        BulkWatchOperationDto dto,
        IWatchService watchService,
        IOutputCacheStore cache,
        CancellationToken ct)
    {
        var result = new BulkOperationResultDto();

        foreach (var id in dto.WatchIds)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    result.Failures[id] = "Invalid ID format";
                    result.FailureCount++;
                    continue;
                }

                var watch = await watchService.GetByIdAsync(guidId, ct);
                if (watch == null)
                {
                    result.Failures[id] = "Watch not found";
                    result.FailureCount++;
                    continue;
                }

                await watchService.EnableWatchAsync(guidId, ct);
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                result.Failures[id] = ex.Message;
                result.FailureCount++;
            }
        }

        await cache.EvictByTagAsync("watches", ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> BulkDisable(
        BulkWatchOperationDto dto,
        IWatchService watchService,
        IOutputCacheStore cache,
        CancellationToken ct)
    {
        var result = new BulkOperationResultDto();

        foreach (var id in dto.WatchIds)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    result.Failures[id] = "Invalid ID format";
                    result.FailureCount++;
                    continue;
                }

                var watch = await watchService.GetByIdAsync(guidId, ct);
                if (watch == null)
                {
                    result.Failures[id] = "Watch not found";
                    result.FailureCount++;
                    continue;
                }

                await watchService.DisableWatchAsync(guidId, ct);
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                result.Failures[id] = ex.Message;
                result.FailureCount++;
            }
        }

        await cache.EvictByTagAsync("watches", ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> BulkDelete(
        BulkWatchOperationDto dto,
        IWatchService watchService,
        IOutputCacheStore cache,
        CancellationToken ct)
    {
        var result = new BulkOperationResultDto();

        foreach (var id in dto.WatchIds)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    result.Failures[id] = "Invalid ID format";
                    result.FailureCount++;
                    continue;
                }

                var watch = await watchService.GetByIdAsync(guidId, ct);
                if (watch == null)
                {
                    result.Failures[id] = "Watch not found";
                    result.FailureCount++;
                    continue;
                }

                await watchService.DeleteWatchAsync(guidId, ct);
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                result.Failures[id] = ex.Message;
                result.FailureCount++;
            }
        }

        await cache.EvictByTagAsync("watches", ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> BulkCheck(
        BulkWatchOperationDto dto,
        IWatchService watchService,
        CancellationToken ct)
    {
        var result = new BulkOperationResultDto();

        // Note: Checks are triggered asynchronously, so success means the check was initiated
        foreach (var id in dto.WatchIds)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    result.Failures[id] = "Invalid ID format";
                    result.FailureCount++;
                    continue;
                }

                var watch = await watchService.GetByIdAsync(guidId, ct);
                if (watch == null)
                {
                    result.Failures[id] = "Watch not found";
                    result.FailureCount++;
                    continue;
                }

                // Fire and forget - don't await the check
                _ = watchService.CheckForChangesAsync(guidId, ct);
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                result.Failures[id] = ex.Message;
                result.FailureCount++;
            }
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> BulkEdit(
        BulkWatchEditDto dto,
        IWatchService watchService,
        ICategoryService categoryService,
        IOutputCacheStore cache,
        CancellationToken ct)
    {
        var result = new BulkOperationResultDto();

        // Validate category if provided
        Guid? categoryGuid = null;
        if (dto.ChangeCategoryId && !string.IsNullOrEmpty(dto.CategoryId))
        {
            if (!Guid.TryParse(dto.CategoryId, out var catId))
            {
                return Results.BadRequest("Invalid category ID format");
            }
            var category = await categoryService.GetByIdAsync(catId, ct);
            if (category == null)
            {
                return Results.BadRequest("Category not found");
            }
            categoryGuid = catId;
        }

        foreach (var id in dto.WatchIds)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    result.Failures[id] = "Invalid ID format";
                    result.FailureCount++;
                    continue;
                }

                var watch = await watchService.GetByIdAsync(guidId, ct);
                if (watch == null)
                {
                    result.Failures[id] = "Watch not found";
                    result.FailureCount++;
                    continue;
                }

                var modified = false;

                // Add tags
                if (dto.AddTags?.Count > 0)
                {
                    var normalizedTags = TagNormalizer.NormalizeList(dto.AddTags);
                    foreach (var tag in normalizedTags)
                    {
                        if (!watch.Tags.Contains(tag))
                        {
                            watch.Tags.Add(tag);
                            modified = true;
                        }
                    }
                }

                // Remove tags
                if (dto.RemoveTags?.Count > 0)
                {
                    var normalizedTags = TagNormalizer.NormalizeList(dto.RemoveTags);
                    foreach (var tag in normalizedTags)
                    {
                        if (watch.Tags.Remove(tag))
                        {
                            watch.TagColors.Remove(tag);
                            modified = true;
                        }
                    }
                }

                // Update category
                if (dto.ChangeCategoryId)
                {
                    watch.CategoryId = categoryGuid;
                    modified = true;
                }

                // Update check interval
                if (dto.CheckInterval.HasValue)
                {
                    watch.CheckInterval = dto.CheckInterval.Value;
                    watch.ScheduleSettings.BaseInterval = dto.CheckInterval.Value;
                    modified = true;
                }

                // Update JavaScript setting
                if (dto.UseJavaScript.HasValue)
                {
                    watch.FetchSettings.UseJavaScript = dto.UseJavaScript.Value;
                    modified = true;
                }

                // Update notifications enabled
                if (dto.NotificationsEnabled.HasValue)
                {
                    watch.Notifications.EmailEnabled = dto.NotificationsEnabled.Value;
                    watch.Notifications.WebhookEnabled = dto.NotificationsEnabled.Value && !string.IsNullOrEmpty(watch.Notifications.WebhookUrl);
                    watch.Notifications.DiscordEnabled = dto.NotificationsEnabled.Value && !string.IsNullOrEmpty(watch.Notifications.DiscordWebhookUrl);
                    modified = true;
                }

                if (modified)
                {
                    await watchService.UpdateWatchAsync(watch, ct);
                }

                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                result.Failures[id] = ex.Message;
                result.FailureCount++;
            }
        }

        await cache.EvictByTagAsync("watches", ct);
        return Results.Ok(result);
    }

    private static WatchDetailDto MapToDetailDto(WatchedSite watch, ChangeSnapshot? snapshot, Category? category)
    {
        // Calculate next check time
        var nextCheck = watch.LastChecked.HasValue 
            ? watch.LastChecked.Value.Add(watch.CheckInterval)
            : DateTime.UtcNow;

        return new WatchDetailDto
        {
            Id = watch.Id.ToString(),
            Url = watch.Url,
            Title = watch.Name,
            Description = watch.Description,
            CssSelector = watch.CssSelector,
            XpathSelector = watch.XPathSelector,
            Tags = watch.Tags.Select(t => new TagDto
            {
                Name = t,
                Color = TagColorGenerator.GetColor(t, watch.TagColors),
                IsColorOverridden = watch.TagColors.ContainsKey(t)
            }).ToList(),
            IgnorePatterns = watch.IgnorePatterns,
            CategoryId = category?.Id.ToString(),
            CategoryName = category?.Name,
            CategoryColor = category?.Color,
            CheckInterval = watch.CheckInterval,
            LastCheck = watch.LastChecked,
            NextCheck = nextCheck,
            LastChanged = watch.LastChanged,
            Status = watch.Status.ToString(),
            IsEnabled = watch.IsEnabled,
            LastError = watch.LastError,
            ConsecutiveFailures = watch.ConsecutiveFailures,
            CreatedAt = watch.CreatedAt,
            UpdatedAt = watch.UpdatedAt,
            LlmProviderOverride = watch.LlmProviderOverride,
            SchemaEnabled = watch.SchemaEnabled,
            Schema = watch.Schema != null ? MapToSchemaDto(watch.Schema) : null,
            FilterRules = watch.FilterRules.Select(MapToFilterRuleDto).ToList(),
            FetchSettings = new FetchSettingsDto
            {
                UseJavaScript = watch.FetchSettings.UseJavaScript,
                WaitForSelector = watch.FetchSettings.WaitForSelector,
                WaitTimeMs = watch.FetchSettings.WaitAfterLoadMs,
                CaptureScreenshot = watch.FetchSettings.Screenshot.IsEnabled,
                TimeoutSeconds = watch.FetchSettings.TimeoutSeconds,
                CustomHeaders = watch.FetchSettings.Headers.ToDictionary(h => h.Key, h => "***"),
                ProxyUrl = watch.FetchSettings.ProxyUrl,
                UserAgent = watch.FetchSettings.UserAgent,
                ViewportWidth = watch.FetchSettings.ViewportWidth,
                ViewportHeight = watch.FetchSettings.ViewportHeight,
                Screenshot = MapScreenshotSettingsToDto(watch.FetchSettings.Screenshot)
            },
            NotificationSettings = new NotificationSettingsDto
            {
                EmailEnabled = watch.Notifications.EmailEnabled,
                EmailRecipients = watch.Notifications.EmailAddress != null ? new List<string> { watch.Notifications.EmailAddress } : new(),
                WebhookEnabled = watch.Notifications.WebhookEnabled,
                WebhookUrl = watch.Notifications.WebhookUrl,
                DiscordEnabled = watch.Notifications.DiscordEnabled,
                DiscordWebhookUrl = watch.Notifications.DiscordWebhookUrl,
                MinimumImportanceToNotify = watch.Notifications.MinimumImportance.ToString(),
                UseLlmSummary = watch.Notifications.UseLlmSummary,
                DefaultChannelName = watch.Notifications.DefaultChannelName,
                Channels = watch.Notifications.Channels.Select(c => new NotificationChannelDto
                {
                    Name = c.Name,
                    Type = c.Type.ToString(),
                    Config = c.Config,
                    IsEnabled = c.IsEnabled
                }).ToList()
            },
            ScheduleSettings = MapScheduleSettingsToDto(watch.ScheduleSettings),
            AverageChangeInterval = watch.AverageChangeInterval,
            LastIntervalAdjustment = watch.LastIntervalAdjustment,
            AutoErrorResolutionEnabled = watch.AutoErrorResolutionEnabled,
            AutoResolutionAttempts = watch.AutoResolutionAttempts,
            MaxAutoResolutionAttempts = watch.MaxAutoResolutionAttempts,
            LastResolutionDiagnosis = watch.LastResolutionDiagnosis,
            LastResolutionAttempt = watch.LastResolutionAttempt,
            SelectorHistory = watch.SelectorHistory.Select(MapSelectorHistoryToDto).ToList(),
            SourceType = (SourceTypeDto)(int)watch.SourceType,
            SearchConfig = watch.SearchConfig is not null ? new SearchConfigDto
            {
                Query = watch.SearchConfig.Query,
                ProviderId = watch.SearchConfig.ProviderId,
                Category = watch.SearchConfig.Category,
                Language = watch.SearchConfig.Language,
                TimeRange = watch.SearchConfig.TimeRange,
                MaxResults = watch.SearchConfig.MaxResults,
                AutoPromotionRules = watch.SearchConfig.AutoPromotionRules.Select(r => new AutoPromotionRuleDto
                {
                    UrlPattern = r.UrlPattern,
                    TitleContains = r.TitleContains,
                    IsEnabled = r.IsEnabled,
                    CssSelector = r.CssSelector
                }).ToList()
            } : null,
            LatestSnapshot = snapshot != null ? new SnapshotDto
            {
                Id = snapshot.Id.ToString(),
                Content = snapshot.Content,
                ContentLength = snapshot.Content.Length,
                ContentHash = snapshot.ContentHash,
                CapturedAt = snapshot.CapturedAt,
                ScreenshotPath = snapshot.ScreenshotPath,
                ElementScreenshotPath = snapshot.ElementScreenshotPath,
                ElementBoundingBox = ParseElementBoundingBox(snapshot.ElementBoundingBoxJson),
                ExtractedObjects = DeserializeExtractedObjects(snapshot.ExtractedObjectsJson)
            } : null
        };
    }

    private static List<ExtractedObjectDto>? DeserializeExtractedObjects(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var objects = System.Text.Json.JsonSerializer.Deserialize<List<ExtractedObject>>(json);
            return objects?.Select(o => new ExtractedObjectDto
            {
                Identity = o.IdentityKey ?? "",
                Fields = o.Fields.Where(f => f.Value != null).ToDictionary(f => f.Key, f => f.Value!),
                Index = o.Index
            }).ToList();
        }
        catch
        {
            return null;
        }
    }

    private static ElementBoundingBoxDto? ParseElementBoundingBox(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new ElementBoundingBoxDto
            {
                X = root.GetProperty("x").GetDouble(),
                Y = root.GetProperty("y").GetDouble(),
                Width = root.GetProperty("width").GetDouble(),
                Height = root.GetProperty("height").GetDouble()
            };
        }
        catch
        {
            return null;
        }
    }
    
    private static SelectorHistoryEntryDto MapSelectorHistoryToDto(SelectorHistoryEntry entry)
    {
        return new SelectorHistoryEntryDto
        {
            ChangedAt = entry.ChangedAt,
            PreviousCssSelector = entry.PreviousCssSelector,
            PreviousXPathSelector = entry.PreviousXPathSelector,
            ChangeReason = entry.ChangeReason,
            Diagnosis = entry.Diagnosis,
            Confidence = entry.Confidence
        };
    }

    private static NotificationChannel MapNotificationChannelFromDto(NotificationChannelDto dto)
    {
        var channelType = Enum.TryParse<NotificationChannelType>(dto.Type, true, out var parsedType)
            ? parsedType
            : NotificationChannelType.Webhook;

        return new NotificationChannel
        {
            Name = dto.Name,
            Type = channelType,
            Config = dto.Config,
            IsEnabled = dto.IsEnabled
        };
    }
    
    private static CheckScheduleSettingsDto MapScheduleSettingsToDto(CheckScheduleSettings settings)
    {
        return new CheckScheduleSettingsDto
        {
            Mode = settings.Mode.ToString(),
            BaseInterval = settings.BaseInterval,
            MinInterval = settings.MinInterval,
            MaxInterval = settings.MaxInterval,
            FrequencyMultiplier = settings.FrequencyMultiplier
        };
    }
    
    private static CheckScheduleSettings? MapScheduleSettingsFromDto(CheckScheduleSettingsDto? dto)
    {
        if (dto == null)
            return null;
            
        return new CheckScheduleSettings
        {
            Mode = Enum.TryParse<CheckScheduleMode>(dto.Mode, out var mode) ? mode : CheckScheduleMode.Fixed,
            BaseInterval = dto.BaseInterval,
            MinInterval = dto.MinInterval,
            MaxInterval = dto.MaxInterval,
            FrequencyMultiplier = dto.FrequencyMultiplier
        };
    }

    private static ScreenshotSettingsDto MapScreenshotSettingsToDto(ScreenshotSettings settings)
    {
        return new ScreenshotSettingsDto
        {
            Mode = settings.Mode.ToString(),
            CaptureOnEveryCheck = settings.CaptureOnEveryCheck,
            CaptureOnChange = settings.CaptureOnChange,
            JpegQuality = settings.JpegQuality,
            Format = settings.Format.ToString(),
            Scale = settings.Scale,
            ElementPadding = settings.ElementPadding,
            HighlightElement = settings.HighlightElement,
            HighlightColor = settings.HighlightColor,
            HighlightBorderWidth = settings.HighlightBorderWidth,
            MaxWidth = settings.MaxWidth,
            MaxHeight = settings.MaxHeight
        };
    }

    private static ScreenshotSettings MapScreenshotSettingsFromDto(ScreenshotSettingsDto? dto)
    {
        if (dto == null)
            return new ScreenshotSettings();
            
        return new ScreenshotSettings
        {
            Mode = Enum.TryParse<ScreenshotMode>(dto.Mode, out var mode) ? mode : ScreenshotMode.None,
            CaptureOnEveryCheck = dto.CaptureOnEveryCheck,
            CaptureOnChange = dto.CaptureOnChange,
            JpegQuality = dto.JpegQuality,
            Format = Enum.TryParse<ScreenshotFormat>(dto.Format, out var format) ? format : ScreenshotFormat.Png,
            Scale = dto.Scale,
            ElementPadding = dto.ElementPadding,
            HighlightElement = dto.HighlightElement,
            HighlightColor = dto.HighlightColor,
            HighlightBorderWidth = dto.HighlightBorderWidth,
            MaxWidth = dto.MaxWidth,
            MaxHeight = dto.MaxHeight
        };
    }

    private static ExtractionSchemaDto MapToSchemaDto(ExtractionSchema schema)
    {
        return new ExtractionSchemaDto
        {
            ItemSelector = schema.ItemSelector,
            Fields = schema.Fields.Select(f => new SchemaFieldDto
            {
                Name = f.Name,
                Type = f.Type.ToString(),
                Selector = f.Selector,
                IsRequired = f.IsRequired,
                IsIdentityField = f.IsIdentityField,
                SampleValue = f.SampleValue,
                Confidence = f.Confidence
            }).ToList(),
            IdentityFieldNames = schema.IdentityFieldNames,
            Version = schema.Version,
            DiffSettings = new ObjectDiffSettingsDto
            {
                Granularity = schema.DiffSettings.Granularity.ToString(),
                EnableImportanceScoring = schema.DiffSettings.EnableImportanceScoring,
                DefaultImportance = schema.DiffSettings.DefaultImportance.ToString()
            }
        };
    }

    private static FilterRuleDto MapToFilterRuleDto(FilterRule rule)
    {
        return new FilterRuleDto
        {
            Id = rule.Id.ToString(),
            Name = rule.Name,
            Description = rule.Description,
            Conditions = rule.Conditions.Select(c => new FilterConditionDto
            {
                FieldName = c.FieldName,
                Operator = c.Operator.ToString(),
                Value = c.Value,
                Negate = c.Negate
            }).ToList(),
            Logic = rule.Logic.ToString(),
            Actions = rule.Actions.Select(a => new FilterActionDto
            {
                Type = a.Type.ToString(),
                Parameters = a.Parameters
            }).ToList(),
            IsEnabled = rule.IsEnabled,
            Priority = rule.Priority,
            StopProcessing = rule.StopProcessing
        };
    }

    private static async Task<IResult> GetPriceHistory(
        string id,
        IRepository<PriceHistoryEntry> priceHistoryRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var watchId))
            return Results.BadRequest("Invalid ID");

        var entries = (await priceHistoryRepo.FindAsync(e => e.WatchId == watchId, ct))
            .OrderByDescending(e => e.Timestamp)
            .Take(100)
            .ToList();

        if (entries.Count == 0)
            return Results.Ok(new PriceHistoryResponseDto());

        var allEntries = entries.OrderBy(e => e.Timestamp).ToList();
        var latest = entries.First();
        var prices = entries.Where(e => e.Value > 0).ToList();

        return Results.Ok(new PriceHistoryResponseDto
        {
            Entries = entries.Select(e => new PriceHistoryEntryDto
            {
                Id = e.Id.ToString(),
                WatchId = e.WatchId.ToString(),
                FieldName = e.FieldName,
                Value = e.Value,
                Currency = e.Currency,
                StockStatus = e.StockStatus?.ToString(),
                RawPriceText = e.RawPriceText,
                RawStockText = e.RawStockText,
                Timestamp = e.Timestamp
            }).ToList(),
            ChartData = allEntries.Select(e => new ChartDataPointDto
            {
                Timestamp = e.Timestamp,
                Value = e.Value,
                Label = e.RawPriceText
            }).ToList(),
            CurrentPrice = latest.Value,
            Currency = latest.Currency,
            CurrentStockStatus = latest.StockStatus?.ToString(),
            MinPrice = prices.Count > 0 ? prices.Min(e => e.Value) : null,
            MinPriceAt = prices.Count > 0 ? prices.MinBy(e => e.Value)?.Timestamp : null,
            MaxPrice = prices.Count > 0 ? prices.Max(e => e.Value) : null,
            MaxPriceAt = prices.Count > 0 ? prices.MaxBy(e => e.Value)?.Timestamp : null,
            AveragePrice = prices.Count > 0 ? prices.Average(e => e.Value) : null,
            TotalEntries = entries.Count
        });
    }

    private static async Task<IResult> GetFieldThresholds(
        string id,
        string fieldName,
        IWatchService watchService,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var watchId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(watchId, ct);
        if (watch == null)
            return Results.NotFound();

        var field = watch.Schema?.Fields?.FirstOrDefault(f =>
            f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field == null)
            return Results.NotFound($"Field '{fieldName}' not found in schema");

        var thresholds = field.AlertThresholds.Select(t => new AlertThresholdDto
        {
            Id = t.Id.ToString(),
            ConditionType = t.ConditionType.ToString(),
            ThresholdValue = t.Value,
            IsEnabled = t.IsEnabled,
            TriggerOnceOnly = t.OneTime,
            HasTriggered = t.TriggerCount > 0,
            CooldownPeriod = t.CooldownPeriod?.ToString(),
            LastTriggeredAt = t.LastTriggeredAt,
            NotificationTemplateId = null // Uses NotificationTemplate string instead
        }).ToList();

        return Results.Ok(thresholds);
    }

    private static async Task<IResult> AddFieldThreshold(
        string id,
        string fieldName,
        AlertThresholdCreateDto dto,
        IWatchService watchService,
        IRepository<WatchedSite> watchRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var watchId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(watchId, ct);
        if (watch == null)
            return Results.NotFound();

        if (watch.Schema?.Fields == null)
            return Results.BadRequest("Watch does not have a schema configured");

        var field = watch.Schema.Fields.FirstOrDefault(f =>
            f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field == null)
            return Results.NotFound($"Field '{fieldName}' not found in watch schema");

        if (!Enum.TryParse<AlertConditionType>(dto.ConditionType, ignoreCase: true, out var conditionType))
            return Results.BadRequest($"Invalid condition type: {dto.ConditionType}");

        TimeSpan? cooldown = null;
        if (!string.IsNullOrEmpty(dto.CooldownPeriod) &&
            TimeSpan.TryParse(dto.CooldownPeriod, out var parsedCooldown))
        {
            cooldown = parsedCooldown;
        }

        var threshold = new FieldAlertThreshold
        {
            ConditionType = conditionType,
            Value = dto.ThresholdValue,
            IsEnabled = dto.IsEnabled,
            OneTime = dto.TriggerOnceOnly,
            CooldownPeriod = cooldown,
            NotificationTemplate = null // Could be expanded to support template strings
        };

        field.AlertThresholds.Add(threshold);
        watch.UpdatedAt = DateTime.UtcNow;
        await watchRepo.UpdateAsync(watch, ct);

        return Results.Created($"/api/watches/{id}/thresholds/{fieldName}/{threshold.Id}", new AlertThresholdDto
        {
            Id = threshold.Id.ToString(),
            ConditionType = threshold.ConditionType.ToString(),
            ThresholdValue = threshold.Value,
            IsEnabled = threshold.IsEnabled,
            TriggerOnceOnly = threshold.OneTime,
            HasTriggered = threshold.TriggerCount > 0,
            CooldownPeriod = threshold.CooldownPeriod?.ToString(),
            LastTriggeredAt = threshold.LastTriggeredAt,
            NotificationTemplateId = null
        });
    }

    private static async Task<IResult> UpdateFieldThreshold(
        string id,
        string fieldName,
        string thresholdId,
        AlertThresholdCreateDto dto,
        IWatchService watchService,
        IRepository<WatchedSite> watchRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var watchId))
            return Results.BadRequest("Invalid watch ID");

        if (!Guid.TryParse(thresholdId, out var thresholdGuid))
            return Results.BadRequest("Invalid threshold ID");

        var watch = await watchService.GetByIdAsync(watchId, ct);
        if (watch == null)
            return Results.NotFound();

        if (watch.Schema?.Fields == null)
            return Results.BadRequest("Watch does not have a schema configured");

        var field = watch.Schema.Fields.FirstOrDefault(f =>
            f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field == null)
            return Results.NotFound($"Field '{fieldName}' not found in watch schema");

        var threshold = field.AlertThresholds.FirstOrDefault(t => t.Id == thresholdGuid);
        if (threshold == null)
            return Results.NotFound($"Threshold '{thresholdId}' not found");

        if (!Enum.TryParse<AlertConditionType>(dto.ConditionType, ignoreCase: true, out var conditionType))
            return Results.BadRequest($"Invalid condition type: {dto.ConditionType}");

        TimeSpan? cooldown = null;
        if (!string.IsNullOrEmpty(dto.CooldownPeriod) &&
            TimeSpan.TryParse(dto.CooldownPeriod, out var parsedCooldown))
        {
            cooldown = parsedCooldown;
        }

        threshold.ConditionType = conditionType;
        threshold.Value = dto.ThresholdValue;
        threshold.IsEnabled = dto.IsEnabled;
        threshold.OneTime = dto.TriggerOnceOnly;
        threshold.CooldownPeriod = cooldown;

        watch.UpdatedAt = DateTime.UtcNow;
        await watchRepo.UpdateAsync(watch, ct);

        return Results.Ok(new AlertThresholdDto
        {
            Id = threshold.Id.ToString(),
            ConditionType = threshold.ConditionType.ToString(),
            ThresholdValue = threshold.Value,
            IsEnabled = threshold.IsEnabled,
            TriggerOnceOnly = threshold.OneTime,
            HasTriggered = threshold.TriggerCount > 0,
            CooldownPeriod = threshold.CooldownPeriod?.ToString(),
            LastTriggeredAt = threshold.LastTriggeredAt,
            NotificationTemplateId = null
        });
    }

    private static async Task<IResult> DeleteFieldThreshold(
        string id,
        string fieldName,
        string thresholdId,
        IWatchService watchService,
        IRepository<WatchedSite> watchRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var watchId))
            return Results.BadRequest("Invalid watch ID");

        if (!Guid.TryParse(thresholdId, out var thresholdGuid))
            return Results.BadRequest("Invalid threshold ID");

        var watch = await watchService.GetByIdAsync(watchId, ct);
        if (watch == null)
            return Results.NotFound();

        if (watch.Schema?.Fields == null)
            return Results.BadRequest("Watch does not have a schema configured");

        var field = watch.Schema.Fields.FirstOrDefault(f =>
            f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field == null)
            return Results.NotFound($"Field '{fieldName}' not found in watch schema");

        var threshold = field.AlertThresholds.FirstOrDefault(t => t.Id == thresholdGuid);
        if (threshold == null)
            return Results.NotFound($"Threshold '{thresholdId}' not found");

        field.AlertThresholds.Remove(threshold);
        watch.UpdatedAt = DateTime.UtcNow;
        await watchRepo.UpdateAsync(watch, ct);

        return Results.NoContent();
    }

    /// <summary>Set pipeline definition JSON on a watch (dev/testing endpoint).</summary>
    private static async Task<IResult> SetPipelineDefinition(
        string id,
        HttpRequest request,
        IWatchService watchService,
        IPipelineValidator pipelineValidator,
        IBlockRegistry blockRegistry,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch is null)
            return Results.NotFound();

        using var reader = new StreamReader(request.Body);
        var json = await reader.ReadToEndAsync(ct);

        // Validate the JSON is a valid pipeline
        var definition = PipelineSerializer.Deserialize(json, logger);
        if (definition is null)
            return Results.BadRequest("Invalid pipeline definition JSON");

        var validation = pipelineValidator.Validate(definition, blockRegistry);
        if (!validation.IsValid)
        {
            var details = string.Join("; ", validation.Errors.Select(e =>
                string.IsNullOrWhiteSpace(e.BlockId)
                    ? e.Message
                    : $"{e.BlockId}: {e.Message}"));
            return Results.BadRequest($"Invalid pipeline definition: {details}");
        }

        watch.PipelineDefinitionJson = json;
        watch.UpdatedAt = DateTime.UtcNow;
        await watchService.UpdateWatchAsync(watch, ct);

        logger.LogInformation("Pipeline definition set for watch {WatchId}: {BlockCount} blocks",
            guidId, definition.Blocks.Count);

        return Results.NoContent();
    }

    private static async Task<IResult> GetPipelineDefinition(
        string id,
        IWatchService watchService,
        IPipelineValidator pipelineValidator,
        IBlockRegistry blockRegistry,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch is null)
            return Results.NotFound();

        var pipelineJson = watch.PipelineDefinitionJson ?? "";
        var hasPipeline = !string.IsNullOrWhiteSpace(pipelineJson);
        var definition = hasPipeline ? PipelineSerializer.Deserialize(pipelineJson, logger) : null;
        var validation = definition is not null
            ? pipelineValidator.Validate(definition, blockRegistry)
            : ChangeDetection.Core.Pipeline.Validation.ValidationResult.Valid();

        var orderedBlocks = definition is not null ? OrderBlocks(definition) : [];
        var blockOrder = orderedBlocks
            .Select((block, index) => new { block.Id, Index = index })
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var dto = new WatchPipelineDto
        {
            WatchId = watch.Id.ToString(),
            WatchName = string.IsNullOrWhiteSpace(watch.Name) ? watch.Url : watch.Name,
            WatchUrl = watch.Url,
            HasPipeline = hasPipeline,
            PipelineJson = pipelineJson,
            BlockCount = definition?.Blocks.Count ?? 0,
            EstimatedExecutionTimeMs = definition is null ? 0d : EstimateExecutionTimeMs(definition),
            LastRunStatus = watch.Status.ToString(),
            LastChecked = watch.LastChecked,
            LastError = watch.LastError,
            IsPipelineValid = definition is not null && validation.IsValid,
            ValidationErrors = definition is null && hasPipeline
                ? ["Stored pipeline JSON could not be parsed."]
                : validation.Errors.Select(e => string.IsNullOrWhiteSpace(e.BlockId) ? e.Message : $"{e.BlockId}: {e.Message}").ToList(),
            ValidationWarnings = validation.Warnings.Select(w => string.IsNullOrWhiteSpace(w.BlockId) ? w.Message : $"{w.BlockId}: {w.Message}").ToList(),
            Blocks = definition is null
                ? []
                : orderedBlocks.Select(block => new WatchPipelineBlockDto
                {
                    Id = block.Id,
                    Type = block.Type,
                    Category = GetBlockCategory(block.Type),
                    Icon = GetBlockIcon(block.Type),
                    ExecutionOrder = blockOrder[block.Id],
                    ConfigJson = block.Config?.GetRawText() ?? "{}",
                    KeyConfigValues = GetKeyConfigValues(block.Config)
                }).ToList(),
            Connections = definition?.Connections.Select(connection => new WatchPipelineConnectionDto
            {
                FromBlockId = connection.FromBlockId,
                FromPort = connection.FromPort,
                ToBlockId = connection.ToBlockId,
                ToPort = connection.ToPort
            }).ToList() ?? []
        };

        return Results.Ok(dto);
    }

    /// <summary>Get pipeline health and execution context for a watch.</summary>
    private static async Task<IResult> GetPipelineStatus(
        string id,
        IWatchService watchService,
        IBlockStateStore stateStore,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch is null)
            return Results.NotFound();

        var hasPipeline = !string.IsNullOrEmpty(watch.PipelineDefinitionJson);

        PipelineDefinition? definition = null;
        if (hasPipeline)
            definition = PipelineSerializer.Deserialize(watch.PipelineDefinitionJson!, logger);

        return Results.Ok(new
        {
            HasPipeline = hasPipeline,
            BlockCount = definition?.Blocks.Count ?? 0,
            Blocks = definition?.Blocks.Select(b => new
            {
                b.Id,
                b.Type,
                b.Position
            }) ?? Enumerable.Empty<object>(),
            Connections = definition?.Connections.Select(c => new
            {
                From = $"{c.FromBlockId}.{c.FromPort}",
                To = $"{c.ToBlockId}.{c.ToPort}"
            }) ?? Enumerable.Empty<object>(),
            WatchStatus = watch.Status.ToString(),
            LastChecked = watch.LastChecked,
            LastError = watch.LastError,
            PipelineMetadata = definition?.Metadata
        });
    }

    /// <summary>Execute pipeline for a watch immediately (dev/testing endpoint).</summary>
    private static async Task<IResult> ExecutePipeline(
        string id,
        IWatchService watchService,
        IPipelineExecutor pipelineExecutor,
        IBlockStateStore stateStore,
        IPipelineRunSummaryStore summaryStore,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch is null)
            return Results.NotFound();

        if (string.IsNullOrEmpty(watch.PipelineDefinitionJson))
            return Results.BadRequest("Watch has no pipeline definition. Use PUT /api/watches/{id}/pipeline first.");

        var definition = PipelineSerializer.Deserialize(watch.PipelineDefinitionJson, logger);
        if (definition is null)
            return Results.BadRequest("Failed to deserialize pipeline definition");

        logger.LogInformation("Manually triggering pipeline execution for watch {WatchId} ({WatchName})",
            guidId, watch.Name);

        var result = await pipelineExecutor.ExecuteAsync(definition, guidId, stateStore, null, ct);

        // Persist pipeline run summary for observability
        var summary = PipelineRunSummaryBuilder.Build(guidId.ToString(), result, definition);
        await summaryStore.SaveAsync(summary, ct);

        logger.LogInformation("Pipeline execution finished for watch {WatchId}: Success={Success}, Blocks={BlockCount}",
            guidId, result.Success, result.BlockResults?.Count ?? 0);

        return Results.Ok(new
        {
            result.Success,
            result.WasBaseline,
            result.IsDegraded,
            result.Error,
            result.ExecutionDurationMs,
            SkippedBlocks = result.SkippedBlockIds,
            OutputData = result.OutputData.HasValue ? TruncateJson(result.OutputData.Value, 5000) : null,
            OutputItemCount = result.OutputData.HasValue && result.OutputData.Value.ValueKind == JsonValueKind.Array
                ? result.OutputData.Value.GetArrayLength()
                : (int?)null,
            BlockResults = result.BlockResults?.Select(kv => new
            {
                BlockId = kv.Key,
                kv.Value.Success,
                Status = kv.Value.Status.ToString(),
                HasOutput = kv.Value.Output is not null,
                kv.Value.Error,
                kv.Value.CacheHit,
                OutputPreview = kv.Value.Output.HasValue
                    ? TruncateJson(kv.Value.Output.Value, 2000)
                    : null,
                OutputItemCount = kv.Value.Output.HasValue && kv.Value.Output.Value.ValueKind == JsonValueKind.Array
                    ? kv.Value.Output.Value.GetArrayLength()
                    : kv.Value.Output.HasValue && kv.Value.Output.Value.ValueKind == JsonValueKind.Object
                        && kv.Value.Output.Value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                        ? items.GetArrayLength()
                        : (int?)null
            })
        });
    }

    private static string? TruncateJson(JsonElement element, int maxChars)
    {
        var json = element.GetRawText();
        return json.Length <= maxChars ? json : json[..maxChars] + "...";
    }

    private static PipelineHealthDto MapToPipelineHealthDto(PipelineRunSummaryEntity summary)
    {
        var blocks = new List<PipelineBlockStatusDto>();
        try
        {
            var blockSummaries = JsonSerializer.Deserialize<List<PipelineBlockSummary>>(summary.BlockSummariesJson);
            if (blockSummaries is not null)
            {
                blocks = blockSummaries.Select(b => new PipelineBlockStatusDto
                {
                    BlockId = b.BlockId,
                    BlockType = b.BlockType,
                    Status = b.Status,
                    DurationMs = b.DurationMs,
                    OutputSizeChars = b.OutputSizeChars,
                    Error = b.Error,
                    CacheHit = b.CacheHit
                }).ToList();
            }
        }
                catch (JsonException ex)
        {
            /* ignore malformed JSON */
            Console.WriteLine($"[WatchEndpoints] Error in MapToPipelineHealthDto: {ex.Message}");
        }

        return new PipelineHealthDto
        {
            Success = summary.Success,
            IsDegraded = summary.IsDegraded,
            ExecutionDurationMs = summary.ExecutionDurationMs,
            ExecutedAt = summary.Timestamp,
            Error = summary.Error,
            BlockCount = blocks.Count,
            CompletedCount = blocks.Count(b => b.Status is "Completed" or "Baseline"),
            FailedCount = blocks.Count(b => b.Status == "Failed"),
            SkippedCount = blocks.Count(b => b.Status == "Skipped"),
            Blocks = blocks
        };
    }

    private static string? DetermineExtractionQuality(
        WatchedSite watch, int? itemCount, PipelineRunSummaryEntity? runSummary)
    {
        // Pipeline failed → "failed"
        if (runSummary is { Success: false })
            return "failed";
        if (watch.Status == WatchStatus.Error)
            return "failed";

        // Checked at least once
        if (!watch.LastChecked.HasValue)
            return null;

        // Items extracted → "ok"
        if (itemCount is > 0)
            return "ok";

        // Checked but 0 items → "empty"
        if (itemCount is 0)
            return "empty";

        return null;
    }

    private static int? CountSnapshotItems(ChangeSnapshot? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ExtractedObjectsJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(snapshot.ExtractedObjectsJson);
            // Handle both direct arrays and ListDiff wrapper objects {"items":[...]}
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return doc.RootElement.GetArrayLength();
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
                return items.GetArrayLength();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<BlockDefinition> OrderBlocks(PipelineDefinition definition)
    {
        var blocks = definition.Blocks.ToList();
        var inDegree = blocks.ToDictionary(block => block.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var adjacency = blocks.ToDictionary(block => block.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var connection in definition.Connections)
        {
            if (inDegree.ContainsKey(connection.ToBlockId))
                inDegree[connection.ToBlockId]++;

            if (adjacency.TryGetValue(connection.FromBlockId, out var downstream))
                downstream.Add(connection.ToBlockId);
        }

        var queue = new Queue<string>(inDegree
            .Where(entry => entry.Value == 0)
            .OrderBy(entry => entry.Key)
            .Select(entry => entry.Key));

        var ordered = new List<BlockDefinition>();
        var blockMap = blocks.ToDictionary(block => block.Id, StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var blockId = queue.Dequeue();
            if (blockMap.TryGetValue(blockId, out var block))
                ordered.Add(block);

            if (!adjacency.TryGetValue(blockId, out var nextBlocks))
                continue;

            foreach (var next in nextBlocks)
            {
                if (!inDegree.ContainsKey(next))
                    continue;

                inDegree[next]--;
                if (inDegree[next] == 0)
                    queue.Enqueue(next);
            }
        }

        foreach (var block in blocks.OrderBy(block => block.Position ?? int.MaxValue))
        {
            if (!ordered.Any(existing => existing.Id.Equals(block.Id, StringComparison.OrdinalIgnoreCase)))
                ordered.Add(block);
        }

        return ordered;
    }

    private static Dictionary<string, string> GetKeyConfigValues(JsonElement? config)
    {
        if (!config.HasValue || config.Value.ValueKind != JsonValueKind.Object)
            return [];

        var configValue = config.Value;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in configValue.EnumerateObject())
        {
            if (result.Count >= 4)
                break;

            var preview = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => $"[{property.Value.GetArrayLength()}]",
                JsonValueKind.Object => "{…}",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(preview))
                result[property.Name] = preview!;
        }

        return result;
    }

    private static double EstimateExecutionTimeMs(PipelineDefinition definition) =>
        definition.Blocks.Sum(block => block.Type.ToLowerInvariant() switch
        {
            "input" => 50d,
            "navigate" => 6000d,
            "fetch" => 2500d,
            "cssfilter" or "xpathfilter" or "filter" => 300d,
            "extractschema" or "extract" or "llmextract" => 1200d,
            "textdiff" or "hashcompare" or "numericdelta" or "listdiff" => 200d,
            "condition" or "route" => 150d,
            "notify" or "notification" => 300d,
            _ => 500d
        });

    private static string GetBlockCategory(string blockType) => blockType.ToLowerInvariant() switch
    {
        "input" or "navigate" or "fetch" => "acquisition",
        "cssfilter" or "xpathfilter" or "filter" or "extractschema" or "extract" or "llmextract" => "extraction",
        "textdiff" or "hashcompare" or "numericdelta" or "listdiff" => "comparison",
        "condition" or "route" or "notify" or "notification" => "decision",
        _ => "other"
    };

    private static string GetBlockIcon(string blockType) => blockType.ToLowerInvariant() switch
    {
        "input" => "📦",
        "navigate" => "🌐",
        "fetch" => "📥",
        "cssfilter" or "xpathfilter" or "filter" => "🔍",
        "extractschema" or "extract" or "llmextract" => "📊",
        "textdiff" or "hashcompare" or "numericdelta" or "listdiff" => "🔄",
        "condition" or "route" => "⚡",
        "notify" or "notification" => "📢",
        _ => "⬜"
    };

    private static async Task<IResult> PromoteSearchResult(
        Guid id,
        PromoteSearchResultDto dto,
        ISearchDiscoveryService discoveryService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Url))
            return Results.BadRequest("URL is required");

        var request = new PromoteSearchResultRequest
        {
            Url = dto.Url,
            Name = dto.Name,
            CssSelector = dto.CssSelector,
            CheckInterval = dto.CheckIntervalMinutes.HasValue
                ? TimeSpan.FromMinutes(dto.CheckIntervalMinutes.Value)
                : null
        };

        var watch = await discoveryService.PromoteResultAsync(id, request, ct);
        if (watch is null)
            return Results.NotFound();

        var detailDto = new WatchDetailDto
        {
            Id = watch.Id.ToString(),
            Url = watch.Url,
            Title = watch.Name,
            IsEnabled = watch.IsEnabled,
            CheckInterval = watch.CheckInterval,
            LastCheck = watch.LastChecked,
            Tags = watch.Tags.Select(t => new TagDto { Name = t, Color = "#6B7280" }).ToList(),
            CssSelector = watch.CssSelector,
            CreatedAt = watch.CreatedAt,
            SourceType = (SourceTypeDto)(int)watch.SourceType
        };

        return Results.Created($"/api/watches/{watch.Id}", detailDto);
    }
}
