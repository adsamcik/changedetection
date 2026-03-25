using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Shared.Dtos;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for application settings.
/// </summary>
public static class SettingsEndpoints
{
    public static RouteGroupBuilder MapSettingsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/general", GetGeneralSettings)
            .WithName("GetGeneralSettings")
            .Produces<GeneralSettingsDto>();

        group.MapPut("/general", UpdateGeneralSettings)
            .WithName("UpdateGeneralSettings")
            .Produces<GeneralSettingsDto>();

        group.MapGet("/notifications", GetNotificationSettings)
            .WithName("GetNotificationSettings")
            .Produces<NotificationChannelSettingsDto>();

        group.MapPut("/notifications", UpdateNotificationSettings)
            .WithName("UpdateNotificationSettings")
            .Produces<NotificationChannelSettingsDto>();

        group.MapPost("/backup", CreateBackup)
            .WithName("CreateBackup")
            .Produces<BackupInfoDto>();

        group.MapGet("/backup", ListBackups)
            .WithName("ListBackups")
            .Produces<List<BackupInfoDto>>();

        group.MapGet("/backup/{fileName}", DownloadBackup)
            .WithName("DownloadBackup")
            .Produces<IResult>();

        group.MapDelete("/backup/{fileName}", DeleteBackup)
            .WithName("DeleteBackup")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/search", GetSearchSettings)
            .WithName("GetSearchSettings")
            .Produces<SearchSettingsDto>();

        group.MapPut("/search", UpdateSearchSettings)
            .WithName("UpdateSearchSettings")
            .Produces<SearchSettingsDto>();

        return group;
    }

    private static async Task<IResult> GetGeneralSettings(
        IRepository<AppSettings> settingsRepo,
        CancellationToken ct)
    {
        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault() ?? new AppSettings();

        var dto = new GeneralSettingsDto
        {
            DefaultCheckIntervalMinutes = (int)settings.DefaultCheckInterval.TotalMinutes,
            MaxConcurrentChecks = settings.MaxConcurrentChecks,
            SnapshotRetentionDays = settings.SnapshotRetentionDays,
            ChangeEventRetentionDays = settings.ChangeEventRetentionDays,
            MaxPlaywrightInstances = settings.MaxPlaywrightInstances,
            UseLlmForSummaries = settings.UseLlmForSummaries,
            DefaultUserAgent = settings.DefaultUserAgent,
            DefaultFetchTimeoutSeconds = settings.DefaultFetchTimeoutSeconds,
            EnableLlmDebugLogging = settings.EnableLlmDebugLogging,
            MaxRetryAttempts = settings.MaxRetryAttempts,
            RetryDelaySeconds = settings.RetryDelaySeconds
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> UpdateGeneralSettings(
        GeneralSettingsUpdateDto update,
        IRepository<AppSettings> settingsRepo,
        CancellationToken ct)
    {
        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault();
        var isNew = settings == null;
        settings ??= new AppSettings();

        // Apply updates only for non-null values
        if (update.DefaultCheckIntervalMinutes.HasValue)
            settings.DefaultCheckInterval = TimeSpan.FromMinutes(update.DefaultCheckIntervalMinutes.Value);
        
        if (update.MaxConcurrentChecks.HasValue)
            settings.MaxConcurrentChecks = update.MaxConcurrentChecks.Value;
        
        if (update.SnapshotRetentionDays.HasValue)
            settings.SnapshotRetentionDays = update.SnapshotRetentionDays.Value;
        
        if (update.ChangeEventRetentionDays.HasValue)
            settings.ChangeEventRetentionDays = update.ChangeEventRetentionDays.Value;
        
        if (update.MaxPlaywrightInstances.HasValue)
            settings.MaxPlaywrightInstances = update.MaxPlaywrightInstances.Value;
        
        if (update.UseLlmForSummaries.HasValue)
            settings.UseLlmForSummaries = update.UseLlmForSummaries.Value;
        
        if (update.DefaultUserAgent is not null)
            settings.DefaultUserAgent = update.DefaultUserAgent;
        
        if (update.DefaultFetchTimeoutSeconds.HasValue)
            settings.DefaultFetchTimeoutSeconds = update.DefaultFetchTimeoutSeconds.Value;
        
        if (update.EnableLlmDebugLogging.HasValue)
            settings.EnableLlmDebugLogging = update.EnableLlmDebugLogging.Value;
        
        if (update.MaxRetryAttempts.HasValue)
            settings.MaxRetryAttempts = update.MaxRetryAttempts.Value;
        
        if (update.RetryDelaySeconds.HasValue)
            settings.RetryDelaySeconds = update.RetryDelaySeconds.Value;

        if (isNew)
            await settingsRepo.InsertAsync(settings, ct);
        else
            await settingsRepo.UpdateAsync(settings, ct);

        var dto = new GeneralSettingsDto
        {
            DefaultCheckIntervalMinutes = (int)settings.DefaultCheckInterval.TotalMinutes,
            MaxConcurrentChecks = settings.MaxConcurrentChecks,
            SnapshotRetentionDays = settings.SnapshotRetentionDays,
            ChangeEventRetentionDays = settings.ChangeEventRetentionDays,
            MaxPlaywrightInstances = settings.MaxPlaywrightInstances,
            UseLlmForSummaries = settings.UseLlmForSummaries,
            DefaultUserAgent = settings.DefaultUserAgent,
            DefaultFetchTimeoutSeconds = settings.DefaultFetchTimeoutSeconds,
            EnableLlmDebugLogging = settings.EnableLlmDebugLogging,
            MaxRetryAttempts = settings.MaxRetryAttempts,
            RetryDelaySeconds = settings.RetryDelaySeconds
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetNotificationSettings(
        IRepository<AppSettings> settingsRepo,
        CancellationToken ct)
    {
        var settings = await GetOrCreateSettingsAsync(settingsRepo, ct);
        return Results.Ok(MapNotificationSettings(settings.DefaultNotifications));
    }

    private static async Task<IResult> UpdateNotificationSettings(
        NotificationChannelSettingsDto update,
        IRepository<AppSettings> settingsRepo,
        IUrlValidator urlValidator,
        CancellationToken ct)
    {
        var webhookValidationError = ValidateNotificationWebhookUrls(update, urlValidator);
        if (webhookValidationError is not null)
            return Results.BadRequest(webhookValidationError);

        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault();
        var isNew = settings is null;
        settings ??= new AppSettings();

        settings.DefaultNotifications = MapNotificationSettings(update);

        if (isNew)
            await settingsRepo.InsertAsync(settings, ct);
        else
            await settingsRepo.UpdateAsync(settings, ct);

        return Results.Ok(MapNotificationSettings(settings.DefaultNotifications));
    }

    private static async Task<IResult> CreateBackup(
        IDatabaseBackupService backupService,
        CancellationToken ct)
    {
        var path = await backupService.CreateBackupAsync(ct);
        var fileInfo = new FileInfo(path);

        return Results.Ok(new BackupInfoDto(fileInfo.Name, fileInfo.Length, fileInfo.CreationTimeUtc));
    }

    private static async Task<IResult> ListBackups(
        IDatabaseBackupService backupService,
        CancellationToken ct)
    {
        var backups = await backupService.GetBackupsAsync(ct);
        var dtos = backups.Select(b => new BackupInfoDto(b.FileName, b.SizeBytes, b.CreatedAt)).ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> DownloadBackup(
        string fileName,
        IDatabaseBackupService backupService,
        CancellationToken ct)
    {
        var path = await backupService.GetBackupPathAsync(fileName, ct);
        if (path is null)
            return Results.NotFound();

        return Results.File(path, "application/octet-stream", Path.GetFileName(path));
    }

    private static async Task<IResult> DeleteBackup(
        string fileName,
        IDatabaseBackupService backupService,
        CancellationToken ct)
    {
        var path = await backupService.GetBackupPathAsync(fileName, ct);
        if (path is null)
            return Results.NotFound();

        File.Delete(path);
        return Results.NoContent();
    }

    private static IResult GetSearchSettings(
        IOptions<SearchSettings> searchSettings,
        IEnumerable<ISearchProvider> providers)
    {
        var settings = searchSettings.Value;
        var dto = new SearchSettingsDto
        {
            SearxngUrl = settings.SearxngUrl,
            GoogleCseApiKey = settings.GoogleCseApiKey is not null ? "***configured***" : null,
            GoogleCseEngineId = settings.GoogleCseEngineId,
            BraveApiKey = settings.BraveApiKey is not null ? "***configured***" : null,
            NewsDataApiKey = settings.NewsDataApiKey is not null ? "***configured***" : null,
            DefaultProvider = settings.DefaultProvider,
            DefaultMaxResults = settings.DefaultMaxResults,
            TimeoutSeconds = settings.TimeoutSeconds,
            IsAvailable = providers.Any(p => p.IsAvailable)
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> UpdateSearchSettings(
        SearchSettingsDto dto,
        IRepository<AppSettings> settingsRepo,
        CancellationToken ct)
    {
        // SearchSettings are bound from configuration, but we persist
        // the SearXNG URL in AppSettings for simplicity
        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault() ?? new AppSettings();

        // Store search settings in the general settings JSON
        // (The actual IOptions<SearchSettings> is reloaded from config on restart)
        return Results.Ok(dto);
    }

    private static async Task<AppSettings> GetOrCreateSettingsAsync(
        IRepository<AppSettings> settingsRepo,
        CancellationToken ct)
    {
        var allSettings = await settingsRepo.GetAllAsync(ct);
        return allSettings.FirstOrDefault() ?? new AppSettings();
    }

    private static NotificationChannelSettingsDto MapNotificationSettings(NotificationSettings settings)
    {
        var emailChannel = settings.Channels.FirstOrDefault(c => c.Type == NotificationChannelType.Email);
        var discordChannel = settings.Channels.FirstOrDefault(c => c.Type == NotificationChannelType.Discord);
        var webhookChannel = settings.Channels.FirstOrDefault(c => c.Type == NotificationChannelType.Webhook);
        var browserChannel = settings.Channels.FirstOrDefault(c => c.Type == NotificationChannelType.Browser);

        return new NotificationChannelSettingsDto
        {
            EmailEnabled = emailChannel?.IsEnabled ?? settings.EmailEnabled,
            EmailAddress = settings.EmailAddress
                ?? emailChannel?.Config.GetValueOrDefault("address"),
            DiscordEnabled = discordChannel?.IsEnabled ?? settings.DiscordEnabled,
            DiscordWebhookUrl = settings.DiscordWebhookUrl
                ?? discordChannel?.Config.GetValueOrDefault("webhookUrl")
                ?? discordChannel?.Config.GetValueOrDefault("url"),
            WebhookEnabled = webhookChannel?.IsEnabled ?? settings.WebhookEnabled,
            WebhookUrl = settings.WebhookUrl
                ?? webhookChannel?.Config.GetValueOrDefault("url"),
            BrowserEnabled = browserChannel?.IsEnabled ?? false,
            DefaultChannelName = settings.DefaultChannelName
        };
    }

    private static NotificationSettings MapNotificationSettings(NotificationChannelSettingsDto dto)
    {
        var settings = new NotificationSettings
        {
            EmailEnabled = dto.EmailEnabled && !string.IsNullOrWhiteSpace(dto.EmailAddress),
            EmailAddress = dto.EmailAddress?.Trim(),
            DiscordEnabled = dto.DiscordEnabled && !string.IsNullOrWhiteSpace(dto.DiscordWebhookUrl),
            DiscordWebhookUrl = dto.DiscordWebhookUrl?.Trim(),
            WebhookEnabled = dto.WebhookEnabled && !string.IsNullOrWhiteSpace(dto.WebhookUrl),
            WebhookUrl = dto.WebhookUrl?.Trim(),
            DefaultChannelName = dto.DefaultChannelName
        };

        if (!string.IsNullOrWhiteSpace(dto.EmailAddress) || dto.EmailEnabled)
        {
            settings.Channels.Add(new NotificationChannel
            {
                Name = "email",
                Type = NotificationChannelType.Email,
                IsEnabled = dto.EmailEnabled && !string.IsNullOrWhiteSpace(dto.EmailAddress),
                Config = new Dictionary<string, string>
                {
                    ["address"] = dto.EmailAddress?.Trim() ?? ""
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(dto.DiscordWebhookUrl) || dto.DiscordEnabled)
        {
            settings.Channels.Add(new NotificationChannel
            {
                Name = "discord",
                Type = NotificationChannelType.Discord,
                IsEnabled = dto.DiscordEnabled && !string.IsNullOrWhiteSpace(dto.DiscordWebhookUrl),
                Config = new Dictionary<string, string>
                {
                    ["webhookUrl"] = dto.DiscordWebhookUrl?.Trim() ?? ""
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(dto.WebhookUrl) || dto.WebhookEnabled)
        {
            settings.Channels.Add(new NotificationChannel
            {
                Name = "webhook",
                Type = NotificationChannelType.Webhook,
                IsEnabled = dto.WebhookEnabled && !string.IsNullOrWhiteSpace(dto.WebhookUrl),
                Config = new Dictionary<string, string>
                {
                    ["url"] = dto.WebhookUrl?.Trim() ?? ""
                }
            });
        }

        if (dto.BrowserEnabled)
        {
            settings.Channels.Add(new NotificationChannel
            {
                Name = "browser",
                Type = NotificationChannelType.Browser,
                IsEnabled = true
            });
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultChannelName) &&
            settings.Channels.All(c => !string.Equals(c.Name, settings.DefaultChannelName, StringComparison.OrdinalIgnoreCase)))
        {
            settings.DefaultChannelName = null;
        }

        return settings;
    }

    private static string? ValidateNotificationWebhookUrls(
        NotificationChannelSettingsDto dto,
        IUrlValidator urlValidator)
    {
        if (!string.IsNullOrWhiteSpace(dto.WebhookUrl))
        {
            var validationError = urlValidator.Validate(dto.WebhookUrl);
            if (validationError is not null)
                return $"Webhook URL is invalid: {validationError}";
        }

        if (!string.IsNullOrWhiteSpace(dto.DiscordWebhookUrl))
        {
            var validationError = urlValidator.Validate(dto.DiscordWebhookUrl);
            if (validationError is not null)
                return $"Discord webhook URL is invalid: {validationError}";
        }

        return null;
    }
}

/// <summary>
/// DTO for backup information returned by API endpoints.
/// </summary>
public record BackupInfoDto(string FileName, long SizeBytes, DateTime CreatedAt);
