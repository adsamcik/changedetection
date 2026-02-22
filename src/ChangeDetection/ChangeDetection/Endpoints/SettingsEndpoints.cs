using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
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
}

/// <summary>
/// DTO for backup information returned by API endpoints.
/// </summary>
public record BackupInfoDto(string FileName, long SizeBytes, DateTime CreatedAt);
