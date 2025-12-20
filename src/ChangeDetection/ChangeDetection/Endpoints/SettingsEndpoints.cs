using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;

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
}
