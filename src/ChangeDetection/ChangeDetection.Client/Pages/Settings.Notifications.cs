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
    private async Task SaveNotificationSettings()
    {
        await SaveNotificationChannels();
    }

    // ========== Notification Methods ==========

    private void SetNotificationsTab()
    {
        activeTab = "notifications";
        _ = PersistTabAsync(activeTab);
        _ = LoadSmtpSettings();
    }

    private void SetTemplatesTab()
    {
        notificationSubTab = "templates";
        _ = LoadTemplates();
        _ = LoadPlaceholders();
    }

    private async Task LoadSmtpSettings()
    {
        isLoadingNotifications = true;
        notificationMessage = null;
        StateHasChanged();

        try
        {
            var notificationResponse = await Http.GetAsync("/api/settings/notifications");
            if (notificationResponse.IsSuccessStatusCode)
            {
                var settings = await notificationResponse.Content.ReadFromJsonAsync<NotificationChannelSettingsDto>();
                if (settings != null)
                {
                    notificationChannels = settings;
                    emailEnabled = settings.EmailEnabled;
                }
            }

            var response = await Http.GetAsync("/api/notifications/smtp");
            if (response.IsSuccessStatusCode)
            {
                var settings = await response.Content.ReadFromJsonAsync<SmtpSettingsDto>();
                if (settings != null)
                {
                    smtpHost = settings.Host;
                    smtpPort = settings.Port;
                    smtpUseSsl = settings.UseSsl;
                    smtpUsername = settings.Username ?? "";
                    fromEmail = settings.FromEmail ?? "";
                    fromName = settings.FromName ?? "Change Detection";
                }
            }
        }
        catch (Exception ex)
        {
            notificationMessage = $"Failed to load SMTP settings: {ex.Message}";
            notificationSuccess = false;
        }
        finally
        {
            isLoadingNotifications = false;
            StateHasChanged();
        }
    }

    private async Task SaveNotificationChannels()
    {
        isSavingNotifications = true;
        notificationMessage = null;
        StateHasChanged();

        try
        {
            if (!string.IsNullOrWhiteSpace(notificationChannels.DefaultChannelName)
                && AvailableNotificationChannels.All(c => c.Value != notificationChannels.DefaultChannelName))
            {
                notificationChannels.DefaultChannelName = null;
            }

            var response = await Http.PutAsJsonAsync("/api/settings/notifications", notificationChannels);
            if (response.IsSuccessStatusCode)
            {
                var saved = await response.Content.ReadFromJsonAsync<NotificationChannelSettingsDto>();
                if (saved != null)
                {
                    notificationChannels = saved;
                    emailEnabled = saved.EmailEnabled;
                }

                notificationMessage = "Notification channel settings saved successfully!";
                notificationSuccess = true;
                isNotificationsDirty = false;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                notificationMessage = $"Failed to save notification channels: {error}";
                notificationSuccess = false;
            }
        }
        catch (Exception ex)
        {
            notificationMessage = $"Failed to save notification channels: {ex.Message}";
            notificationSuccess = false;
        }
        finally
        {
            isSavingNotifications = false;
            StateHasChanged();
        }
    }

    private async Task SaveSmtpSettings()
    {
        isSavingNotifications = true;
        notificationMessage = null;
        StateHasChanged();

        try
        {
            var update = new SmtpSettingsUpdateDto
            {
                Host = smtpHost,
                Port = smtpPort,
                UseSsl = smtpUseSsl,
                Username = smtpUsername,
                Password = string.IsNullOrWhiteSpace(smtpPassword) ? null : smtpPassword,
                FromEmail = fromEmail,
                FromName = fromName
            };

            var response = await Http.PutAsJsonAsync("/api/notifications/smtp", update);
            if (response.IsSuccessStatusCode)
            {
                notificationMessage = "SMTP settings saved successfully!";
                notificationSuccess = true;
                smtpPassword = ""; // Clear password field after save
                isNotificationsDirty = false; // Reset dirty state
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                notificationMessage = $"Failed to save SMTP settings: {error}";
                notificationSuccess = false;
            }
        }
        catch (Exception ex)
        {
            notificationMessage = $"Failed to save SMTP settings: {ex.Message}";
            notificationSuccess = false;
        }
        finally
        {
            isSavingNotifications = false;
            StateHasChanged();
        }
    }

    private async Task TestEmailNotification()
    {
        await TestNotificationChannel("Email", fromEmail);
    }

    private async Task TestNotificationChannel(string channelType, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            notificationMessage = $"Please enter a target value for {channelType} notifications.";
            notificationSuccess = false;
            return;
        }

        isSavingNotifications = true;
        notificationMessage = null;
        StateHasChanged();

        try
        {
            var testDto = new TestNotificationDto
            {
                ChannelType = channelType,
                Target = target
            };

            var response = await Http.PostAsJsonAsync("/api/notifications/test", testDto);
            var result = await response.Content.ReadFromJsonAsync<TestNotificationResultDto>();

            if (result?.Success == true)
            {
                notificationMessage = result.Details ?? "Test notification validated successfully!";
                notificationSuccess = true;
            }
            else
            {
                notificationMessage = $"Test failed: {result?.ErrorMessage ?? "Unknown error"}";
                notificationSuccess = false;
            }
        }
        catch (Exception ex)
        {
            notificationMessage = $"Test failed: {ex.Message}";
            notificationSuccess = false;
        }
        finally
        {
            isSavingNotifications = false;
            StateHasChanged();
        }
    }

    private async Task LoadTemplates()
    {
        isLoadingTemplates = true;
        StateHasChanged();

        try
        {
            var response = await Http.GetAsync("/api/notifications/templates");
            if (response.IsSuccessStatusCode)
            {
                templates = await response.Content.ReadFromJsonAsync<List<NotificationTemplateDto>>() ?? new();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error in LoadTemplates: {ex.Message}");
        }
        finally
        {
            isLoadingTemplates = false;
            StateHasChanged();
        }
    }

    private async Task LoadPlaceholders()
    {
        try
        {
            var response = await Http.GetAsync("/api/notifications/placeholders");
            if (response.IsSuccessStatusCode)
            {
                placeholders = await response.Content.ReadFromJsonAsync<List<PlaceholderInfoDto>>() ?? new();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error in LoadPlaceholders: {ex.Message}");
        }
    }

    private void ShowAddTemplate()
    {
        editingTemplate = null;
        templateForm = new NotificationTemplateCreateDto
        {
            Name = "My Custom Template",
            Type = "ContentChange"
        };
        templateModalError = null;
        showTemplateModal = true;
    }

    private void EditTemplate(NotificationTemplateDto template)
    {
        editingTemplate = template;
        templateForm = new NotificationTemplateCreateDto
        {
            Name = template.IsBuiltIn ? $"{template.Name} (Custom)" : template.Name,
            Type = template.Type,
            GenerateLlmSummary = template.GenerateLlmSummary,
            EmailSubjectTemplate = template.EmailSubjectTemplate,
            EmailBodyHtmlTemplate = template.EmailBodyHtmlTemplate,
            EmailBodyTextTemplate = template.EmailBodyTextTemplate,
            DiscordTitleTemplate = template.DiscordTitleTemplate,
            DiscordBodyTemplate = template.DiscordBodyTemplate,
            WebhookPayloadTemplate = template.WebhookPayloadTemplate
        };
        templateModalError = null;
        showTemplateModal = true;
    }

    private void CloseTemplateModal()
    {
        showTemplateModal = false;
        editingTemplate = null;
    }

    private async Task SaveTemplate()
    {
        if (string.IsNullOrWhiteSpace(templateForm.Name))
        {
            templateModalError = "Template name is required.";
            return;
        }

        isSavingTemplate = true;
        templateModalError = null;
        StateHasChanged();

        try
        {
            HttpResponseMessage response;
            if (editingTemplate != null && !editingTemplate.IsBuiltIn)
            {
                response = await Http.PutAsJsonAsync($"/api/notifications/templates/{editingTemplate.Id}", templateForm);
            }
            else
            {
                response = await Http.PostAsJsonAsync("/api/notifications/templates", templateForm);
            }

            if (response.IsSuccessStatusCode)
            {
                CloseTemplateModal();
                await LoadTemplates();
            }
            else
            {
                templateModalError = await response.Content.ReadAsStringAsync();
            }
        }
        catch (Exception ex)
        {
            templateModalError = ex.Message;
        }
        finally
        {
            isSavingTemplate = false;
            StateHasChanged();
        }
    }

    private async Task DeleteTemplate(NotificationTemplateDto template)
    {
        if (template.IsBuiltIn) return;

        try
        {
            var response = await Http.DeleteAsync($"/api/notifications/templates/{template.Id}");
            if (response.IsSuccessStatusCode)
            {
                await LoadTemplates();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error in LoadTemplates: {ex.Message}");
        }
    }

    private static string FormatTemplateType(string type) => type switch
    {
        "ContentChange" => "📄 Content Change",
        "PriceAlert" => "💰 Price Alert",
        "StockAlert" => "📦 Stock Alert",
        "ThresholdTriggered" => "⚠️ Threshold",
        "ItemAdded" => "➕ Item Added",
        "ItemRemoved" => "➖ Item Removed",
        "SchemaDrift" => "⚙️ Schema Drift",
        _ => type
    };

}
