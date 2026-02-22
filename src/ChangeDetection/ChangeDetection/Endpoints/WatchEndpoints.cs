using ChangeDetection.Core;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Shared.Dtos;
using Microsoft.AspNetCore.OutputCaching;

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
        ICategoryService categoryService,
        CancellationToken ct)
    {
        var watches = await watchService.GetAllAsync(ct);
        var categories = (await categoryService.GetAllAsync(ct)).ToDictionary(c => c.Id);
        var dtos = new List<WatchListItemDto>();

        foreach (var watch in watches)
        {
            var changeCount = await eventRepo.CountAsync(e => e.WatchedSiteId == watch.Id, ct);
            var unviewedChanges = (await eventRepo.FindAsync(e => e.WatchedSiteId == watch.Id && !e.IsViewed, ct)).ToList();
            var latestChange = (await eventRepo.FindAsync(e => e.WatchedSiteId == watch.Id, ct))
                .OrderByDescending(e => e.DetectedAt)
                .FirstOrDefault();
            
            // Get category info
            Category? category = null;
            if (watch.CategoryId.HasValue && categories.TryGetValue(watch.CategoryId.Value, out var cat))
                category = cat;
            
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
                CategoryId = category?.Id.ToString(),
                CategoryName = category?.Name,
                CategoryColor = category?.Color,
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

        return Results.Ok(MapToDetailDto(watch, latestSnapshot, category));
    }

    private static async Task<IResult> CreateWatch(
        WatchCreateDto dto,
        IWatchService watchService,
        ICategoryService categoryService,
        CancellationToken ct)
    {
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
            ScheduleSettings = MapScheduleSettingsFromDto(dto.ScheduleSettings)
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
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var watch = await watchService.GetByIdAsync(guidId, ct);
        if (watch == null)
            return Results.NotFound();

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
                CustomHeaders = watch.FetchSettings.Headers,
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

        watch.PipelineDefinitionJson = json;
        watch.UpdatedAt = DateTime.UtcNow;
        await watchService.UpdateWatchAsync(watch, ct);

        logger.LogInformation("Pipeline definition set for watch {WatchId}: {BlockCount} blocks",
            guidId, definition.Blocks.Count);

        return Results.NoContent();
    }

    /// <summary>Execute pipeline for a watch immediately (dev/testing endpoint).</summary>
    private static async Task<IResult> ExecutePipeline(
        string id,
        IWatchService watchService,
        IPipelineExecutor pipelineExecutor,
        IBlockStateStore stateStore,
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
            BlockResults = result.BlockResults.Select(kv => new
            {
                BlockId = kv.Key,
                kv.Value.Success,
                kv.Value.Status,
                HasOutput = kv.Value.Output is not null,
                kv.Value.Error
            })
        });
    }

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
