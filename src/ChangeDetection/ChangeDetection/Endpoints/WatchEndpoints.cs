using ChangeDetection.Core;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;

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
            .Produces<List<WatchListItemDto>>();

        group.MapGet("/{id}", GetWatchById)
            .WithName("GetWatchById")
            .Produces<WatchDetailDto>()
            .Produces(404);

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
            CategoryId = dto.CategoryId != null ? Guid.Parse(dto.CategoryId) : null
        };

        if (dto.FetchSettings != null)
        {
            request.UseJavaScript = dto.FetchSettings.UseJavaScript;
            request.FetchSettings = new FetchSettings
            {
                UseJavaScript = dto.FetchSettings.UseJavaScript,
                WaitForSelector = dto.FetchSettings.WaitForSelector,
                WaitAfterLoadMs = dto.FetchSettings.WaitTimeMs,
                CaptureScreenshot = dto.FetchSettings.CaptureScreenshot,
                TimeoutSeconds = dto.FetchSettings.TimeoutSeconds,
                Headers = dto.FetchSettings.CustomHeaders ?? new()
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

        if (dto.FetchSettings != null)
        {
            watch.FetchSettings.UseJavaScript = dto.FetchSettings.UseJavaScript;
            watch.FetchSettings.WaitForSelector = dto.FetchSettings.WaitForSelector;
            watch.FetchSettings.WaitAfterLoadMs = dto.FetchSettings.WaitTimeMs;
            watch.FetchSettings.CaptureScreenshot = dto.FetchSettings.CaptureScreenshot;
            watch.FetchSettings.TimeoutSeconds = dto.FetchSettings.TimeoutSeconds;
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
            Status = watch.Status.ToString(),
            IsEnabled = watch.IsEnabled,
            LastError = watch.LastError,
            CreatedAt = watch.CreatedAt,
            FetchSettings = new FetchSettingsDto
            {
                UseJavaScript = watch.FetchSettings.UseJavaScript,
                WaitForSelector = watch.FetchSettings.WaitForSelector,
                WaitTimeMs = watch.FetchSettings.WaitAfterLoadMs,
                CaptureScreenshot = watch.FetchSettings.CaptureScreenshot,
                TimeoutSeconds = watch.FetchSettings.TimeoutSeconds,
                CustomHeaders = watch.FetchSettings.Headers
            },
            NotificationSettings = new NotificationSettingsDto
            {
                EmailEnabled = watch.Notifications.EmailEnabled,
                EmailRecipients = watch.Notifications.EmailAddress != null ? new List<string> { watch.Notifications.EmailAddress } : new(),
                WebhookEnabled = watch.Notifications.WebhookEnabled,
                WebhookUrl = watch.Notifications.WebhookUrl,
                DiscordEnabled = watch.Notifications.DiscordEnabled,
                DiscordWebhookUrl = watch.Notifications.DiscordWebhookUrl,
                MinimumImportanceToNotify = watch.Notifications.MinimumImportance.ToString()
            },
            LatestSnapshot = snapshot != null ? new SnapshotDto
            {
                Id = snapshot.Id.ToString(),
                Content = snapshot.Content,
                ContentLength = snapshot.Content.Length,
                ContentHash = snapshot.ContentHash,
                CapturedAt = snapshot.CapturedAt,
                ScreenshotPath = snapshot.ScreenshotPath
            } : null
        };
    }
}
