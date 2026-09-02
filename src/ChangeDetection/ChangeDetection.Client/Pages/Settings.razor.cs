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
    private string activeTab = "providers";
    private List<LlmProviderDto> providers = new();
    private LlmUsageStatsDto? usageStats;
    private bool isLoadingProviders = true;
    private bool showProviderModal = false;
    private LlmProviderDto? editingProvider;
    private LlmProviderCreateDto providerForm = new();
    private string? providerModalError;
    private bool isSavingProvider = false;

    // Search functionality
    private string searchQuery = "";
    private HashSet<string> matchingTabs = new();

    private static readonly SearchableSettingItem[] SearchableSettings =
    [
        new("providers", ["llm", "ai", "openai", "gemini", "provider", "model", "token", "api", "azure", "ollama", "claude", "anthropic", "cost", "priority"], "LLM Providers"),
        new("logs", ["log", "history", "usage", "cost", "debug", "request", "response", "error", "circuit", "retry", "fallback"], "LLM Logs"),
        new("notifications", ["email", "smtp", "webhook", "discord", "notification", "alert", "ssl", "tls", "template"], "Notifications"),
        new("general", ["interval", "schedule", "backup", "export", "import", "theme", "retention", "timeout", "playwright", "browser", "user agent", "concurrent", "retry", "fetch"], "General"),
    ];

    private record SearchableSettingItem(string Tab, string[] Keywords, string DisplayName);

    // Notification settings
    private bool emailEnabled = false;
    private string smtpHost = "";
    private int smtpPort = 587;
    private bool smtpUseSsl = true;
    private string smtpUsername = "";
    private string smtpPassword = "";
    private string fromEmail = "";
    private string fromName = "";
    private bool isLoadingNotifications = false;
    private bool isSavingNotifications = false;
    private string? notificationMessage;
    private bool notificationSuccess = false;
    private NotificationChannelSettingsDto notificationChannels = new();
    private List<PlaceholderInfoDto> placeholders = new();
    private string notificationSubTab = "smtp";
    private bool isNotificationsDirty = false;

    // General settings
    private int defaultCheckInterval = 60;
    private int maxConcurrentChecks = 5;
    private int snapshotRetentionDays = 30;
    private int changeEventRetentionDays = 90;
    private int maxPlaywrightInstances = 3;
    private bool useLlmForSummaries = true;
    private string defaultUserAgent = "";
    private int defaultFetchTimeoutSeconds = 30;
    private bool enableLlmDebugLogging = false;
    private int maxRetryAttempts = 3;
    private int retryDelaySeconds = 60;
    private bool isLoadingGeneralSettings = false;
    private bool isSavingGeneralSettings = false;
    private string? generalSettingsMessage;
    private bool generalSettingsSuccess = false;
    private bool isGeneralSettingsDirty = false;

    // Notification templates
    private List<NotificationTemplateDto> templates = new();
    private bool isLoadingTemplates = false;
    private bool showTemplateModal = false;
    private NotificationTemplateDto? editingTemplate;
    private NotificationTemplateCreateDto templateForm = new() { Name = "", Type = "ContentChange" };
    private string? templateModalError;
    private bool isSavingTemplate = false;

    // LLM Logs
    private List<LlmLogEntryDto> llmLogs = new();
    private bool isLoadingLogs = false;
    private string logLevelFilter = "";
    private string logCategoryFilter = "";
    private bool groupByRequest = true;

    private IEnumerable<LlmLogEntryDto> FilteredLogs => llmLogs
        .Where(log => string.IsNullOrEmpty(logLevelFilter) || log.Level == logLevelFilter)
        .Where(log => string.IsNullOrEmpty(logCategoryFilter) || log.Category == logCategoryFilter);

    /// <summary>
    /// Groups logs by RequestId for paired request/response display.
    /// Returns groups ordered by the most recent timestamp in each group.
    /// </summary>
    private IEnumerable<LlmLogGroup> GroupedLogs
    {
        get
        {
            var filtered = FilteredLogs.ToList();

            // Group logs that have a RequestId
            var grouped = filtered
                .Where(l => l.RequestId.HasValue)
                .GroupBy(l => l.RequestId!.Value)
                .Select(g => new LlmLogGroup
                {
                    RequestId = g.Key,
                    Request = g.FirstOrDefault(l => l.Category == "Request"),
                    Response = g.FirstOrDefault(l => l.Category == "Response"),
                    Error = g.FirstOrDefault(l => l.Category == "Error"),
                    Timestamp = g.Max(l => l.Timestamp)
                })
                .ToList();

            // Add ungrouped logs (circuit breaker, fallback, retry, etc.)
            var ungrouped = filtered
                .Where(l => !l.RequestId.HasValue)
                .Select(l => new LlmLogGroup
                {
                    RequestId = null,
                    Standalone = l,
                    Timestamp = l.Timestamp
                });

            return grouped.Concat(ungrouped).OrderByDescending(g => g.Timestamp);
        }
    }

    /// <summary>
    /// Represents a group of related LLM log entries (request + response/error).
    /// </summary>
    private class LlmLogGroup
    {
        public Guid? RequestId { get; set; }
        public LlmLogEntryDto? Request { get; set; }
        public LlmLogEntryDto? Response { get; set; }
        public LlmLogEntryDto? Error { get; set; }
        public LlmLogEntryDto? Standalone { get; set; }
        public DateTime Timestamp { get; set; }

        public bool IsGrouped => RequestId.HasValue;
        public bool HasResponse => Response != null;
        public bool HasError => Error != null;
        public string ProviderName => Request?.ProviderName ?? Response?.ProviderName ?? Error?.ProviderName ?? Standalone?.ProviderName ?? "Unknown";
        public string? Model => Request?.Model ?? Response?.Model ?? Error?.Model ?? Standalone?.Model;
        public long? DurationMs => Response?.DurationMs ?? Error?.DurationMs;
        public int? InputTokens => Response?.InputTokens;
        public int? OutputTokens => Response?.OutputTokens;

        public string StatusClass => HasError ? "level-error" : (HasResponse ? "level-info" : "level-debug");
        public string StatusLabel => HasError ? "Error" : (HasResponse ? "Success" : "Pending");
    }

    // Valid tab values for localStorage validation
    private static readonly HashSet<string> ValidTabs = ["providers", "logs", "notifications", "general"];

    private IEnumerable<(string Value, string Label)> AvailableNotificationChannels
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(notificationChannels.EmailAddress))
                yield return ("email", "Email");
            if (!string.IsNullOrWhiteSpace(notificationChannels.DiscordWebhookUrl))
                yield return ("discord", "Discord");
            if (!string.IsNullOrWhiteSpace(notificationChannels.WebhookUrl))
                yield return ("webhook", "Webhook");
            if (notificationChannels.BrowserEnabled)
                yield return ("browser", "Browser");
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadProviders();
        await LoadUsageStats();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Restore persisted tab from localStorage
            var savedTab = await LocalStorage.GetItemAsync(LocalStorageKeys.SettingsTab);
            if (!string.IsNullOrEmpty(savedTab) && ValidTabs.Contains(savedTab) && savedTab != activeTab)
            {
                // Set tab and trigger any associated data loading
                switch (savedTab)
                {
                    case "providers":
                        SetProvidersTab();
                        break;
                    case "logs":
                        SetLogsTab();
                        break;
                    case "notifications":
                        SetNotificationsTab();
                        break;
                    case "general":
                        SetGeneralTab();
                        break;
                }
                StateHasChanged();
            }
        }
    }

    private async Task PersistTabAsync(string tab)
    {
        await LocalStorage.SetItemAsync(LocalStorageKeys.SettingsTab, tab);
    }

    private void HandleSetProvidersTab() => SetProvidersTab();
    private void HandleSetLogsTab() => SetLogsTab();
    private void HandleSetNotificationsTab() => SetNotificationsTab();
    private void HandleSetGeneralTab() => SetGeneralTab();

    private void SetProvidersTab()
    {
        activeTab = "providers";
        _ = PersistTabAsync(activeTab);
    }
    private void SetLogsTab()
    {
        activeTab = "logs";
        _ = PersistTabAsync(activeTab);
        _ = LoadLogs();
    }
    private void SetGeneralTab()
    {
        activeTab = "general";
        _ = PersistTabAsync(activeTab);
        _ = LoadGeneralSettings();
    }

    private void OnSearchChanged()
    {
        UpdateMatchingTabs();

        // Auto-switch to first matching tab if current tab doesn't match
        if (!string.IsNullOrWhiteSpace(searchQuery) && matchingTabs.Any() && !matchingTabs.Contains(activeTab))
        {
            var firstMatchingTab = matchingTabs.First();
            switch (firstMatchingTab)
            {
                case "providers":
                    SetProvidersTab();
                    break;
                case "logs":
                    SetLogsTab();
                    break;
                case "notifications":
                    SetNotificationsTab();
                    break;
                case "general":
                    SetGeneralTab();
                    break;
            }
        }
    }

    private void UpdateMatchingTabs()
    {
        matchingTabs.Clear();

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return;
        }

        var query = searchQuery.Trim().ToLowerInvariant();

        foreach (var item in SearchableSettings)
        {
            // Check if display name contains search term
            if (item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matchingTabs.Add(item.Tab);
                continue;
            }

            // Check keywords
            if (item.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                matchingTabs.Add(item.Tab);
            }
        }
    }

    private void ClearSearch()
    {
        searchQuery = "";
        matchingTabs.Clear();
    }

    private string GetTabDimmedClass(string tab)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return "";
        }

        return matchingTabs.Contains(tab) ? "" : "tab-dimmed";
    }

    private string GetTabHighlightClass(string tab)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return "";
        }

        return matchingTabs.Contains(tab) ? "tab-highlighted" : "";
    }

    private string GetSectionDimmedClass(string tab)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return "";
        }

        return matchingTabs.Contains(tab) ? "" : "section-dimmed";
    }

    private MarkupString GetHighlightedTabName(string displayName, string tab)
    {
        if (string.IsNullOrWhiteSpace(searchQuery) || !matchingTabs.Contains(tab))
        {
            return new MarkupString(displayName);
        }

        var query = searchQuery.Trim();
        var index = displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase);

        if (index >= 0)
        {
            var before = displayName[..index];
            var match = displayName.Substring(index, query.Length);
            var after = displayName[(index + query.Length)..];
            return new MarkupString($"{before}<mark class=\"search-highlight\">{match}</mark>{after}");
        }

        return new MarkupString(displayName);
    }
}
