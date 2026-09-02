using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ChangeDetection.Client.Components;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Client.Pages;

public partial class Settings
{
    private async Task LoadGeneralSettings()
    {
        isLoadingGeneralSettings = true;
        generalSettingsMessage = null;
        StateHasChanged();

        try
        {
            var response = await Http.GetAsync("/api/settings/general");
            if (response.IsSuccessStatusCode)
            {
                var settings = await response.Content.ReadFromJsonAsync<GeneralSettingsDto>();
                if (settings != null)
                {
                    defaultCheckInterval = settings.DefaultCheckIntervalMinutes;
                    maxConcurrentChecks = settings.MaxConcurrentChecks;
                    snapshotRetentionDays = settings.SnapshotRetentionDays;
                    changeEventRetentionDays = settings.ChangeEventRetentionDays;
                    maxPlaywrightInstances = settings.MaxPlaywrightInstances;
                    useLlmForSummaries = settings.UseLlmForSummaries;
                    defaultUserAgent = settings.DefaultUserAgent ?? "";
                    defaultFetchTimeoutSeconds = settings.DefaultFetchTimeoutSeconds;
                    enableLlmDebugLogging = settings.EnableLlmDebugLogging;
                    maxRetryAttempts = settings.MaxRetryAttempts;
                    retryDelaySeconds = settings.RetryDelaySeconds;
                }
            }
        }
        catch (Exception ex)
        {
            generalSettingsMessage = $"Failed to load settings: {ex.Message}";
            generalSettingsSuccess = false;
        }
        finally
        {
            isLoadingGeneralSettings = false;
            StateHasChanged();
        }
    }

    private async Task SaveGeneralSettings()
    {
        isSavingGeneralSettings = true;
        generalSettingsMessage = null;
        StateHasChanged();

        try
        {
            var update = new GeneralSettingsUpdateDto
            {
                DefaultCheckIntervalMinutes = defaultCheckInterval,
                MaxConcurrentChecks = maxConcurrentChecks,
                SnapshotRetentionDays = snapshotRetentionDays,
                ChangeEventRetentionDays = changeEventRetentionDays,
                MaxPlaywrightInstances = maxPlaywrightInstances,
                UseLlmForSummaries = useLlmForSummaries,
                DefaultUserAgent = string.IsNullOrWhiteSpace(defaultUserAgent) ? null : defaultUserAgent,
                DefaultFetchTimeoutSeconds = defaultFetchTimeoutSeconds,
                EnableLlmDebugLogging = enableLlmDebugLogging,
                MaxRetryAttempts = maxRetryAttempts,
                RetryDelaySeconds = retryDelaySeconds
            };

            var response = await Http.PutAsJsonAsync("/api/settings/general", update);
            if (response.IsSuccessStatusCode)
            {
                generalSettingsMessage = "Settings saved successfully!";
                generalSettingsSuccess = true;
                isGeneralSettingsDirty = false; // Reset dirty state
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                generalSettingsMessage = $"Failed to save settings: {error}";
                generalSettingsSuccess = false;
            }
        }
        catch (Exception ex)
        {
            generalSettingsMessage = $"Failed to save settings: {ex.Message}";
            generalSettingsSuccess = false;
        }
        finally
        {
            isSavingGeneralSettings = false;
            StateHasChanged();
        }
    }

    private void MarkNotificationsDirty()
    {
        if (!isNotificationsDirty)
        {
            isNotificationsDirty = true;
        }
    }

    private void MarkGeneralSettingsDirty()
    {
        if (!isGeneralSettingsDirty)
        {
            isGeneralSettingsDirty = true;
        }
    }

    // ========== Search Methods ==========

}
