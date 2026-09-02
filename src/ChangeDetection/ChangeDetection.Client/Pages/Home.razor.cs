using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using ChangeDetection.Client.Components;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Client.Pages;

public partial class Home
{
    [Parameter]
    public string? ViewId { get; set; }

    private List<WatchListItemDto> watches = [];
    private List<ChangeListItemDto> recentChanges = [];
    private List<WatchGroupListItemDto> watchGroups = [];
    private Dictionary<string, WatchGroupDetailDto> expandedGroupDetails = [];
    private Dictionary<string, WatchGroupHealthDto> expandedGroupHealth = [];
    private Dictionary<string, ProcessingActivity> activeProcessing = [];
    private bool isLoading = true;
    private string? error;
    private string? creationMessage;
    private bool creationIsError;

    // Activity feed grouping state
    private HashSet<string> expandedGroups = [];
    private HashSet<string> snoozedWatchIds = [];
    private Dictionary<string, DateTime> snoozeExpirations = [];
    private const string SnoozeStorageKey = "changedetection_snoozed_watches";

    // Bulk editing state
    private bool isBulkMode;
    private HashSet<string> selectedWatchIds = [];
    private bool isBulkOperating;
    private string? bulkOperationMessage;
    private bool bulkOperationIsError;
    private bool showBulkEditDialog;
    private bool showBulkDeleteConfirm;

    // Bulk edit form model
    private BulkEditModel bulkEditModel = new();

    private bool IsAllSelected => FilteredWatches.Any() && FilteredWatches.All(w => selectedWatchIds.Contains(w.Id));

    // Helper property for check interval input
    private int checkIntervalMinutes
    {
        get => (int)(bulkEditModel.CheckInterval?.TotalMinutes ?? 60);
        set => bulkEditModel.CheckInterval = TimeSpan.FromMinutes(value);
    }

    /// <summary>
    /// Represents a watch currently being checked.
    /// </summary>
    private record ProcessingActivity(string WatchId, string WatchName, DateTime StartedAt);

    /// <summary>
    /// Represents a watch being checked, for display in the activity feed.
    /// </summary>
    private record CheckingWatch(string Id, string Name, DateTime? StartedAt);

    /// <summary>
    /// Gets all watches currently being checked, combining SignalR updates with DB state.
    /// SignalR updates take precedence as they have more accurate timing.
    /// </summary>
    private IEnumerable<CheckingWatch> GetCheckingWatches()
    {
        // Track which watches are already in activeProcessing
        var signalRWatchIds = activeProcessing.Keys.ToHashSet();

        // Return SignalR-tracked watches first (they have accurate start times)
        foreach (var processing in activeProcessing.Values.OrderByDescending(p => p.StartedAt))
        {
            yield return new CheckingWatch(processing.WatchId, processing.WatchName, processing.StartedAt);
        }

        // Then add any watches from the API that have Checking status but aren't in SignalR yet
        // (e.g., checks that started before the page loaded)
        foreach (var watch in watches.Where(w => w.Status == "Checking" && !signalRWatchIds.Contains(w.Id)))
        {
            yield return new CheckingWatch(watch.Id, watch.Title ?? watch.Url, watch.LastCheck);
        }
    }

    // SignalR connection
    private HubConnection? hubConnection;

    // Filters
    private string? activeStatusFilter;
    private bool changedRecentlyFilter;
    private string searchText = "";

    // New fields
    private bool isCheckingAll;

    // Cache fields for FilteredWatches
    private IReadOnlyList<WatchListItemDto>? _cachedFilteredWatches;
    private string? _lastSearchText;
    private string? _lastStatusFilter;
    private bool? _lastChangedRecentlyFilter;
    private int _lastWatchesCount;

    // Cache fields for health counts
    private (int healthy, int checking, int error, int paused) _healthCounts;
    private int _degradedCount;
    private int _setupNeededCount;
    private bool _healthCountsValid;

    protected override async Task OnInitializedAsync()
    {
        // Subscribe to keyboard shortcut events
        ShortcutService.OnFocusSearch += HandleFocusSearch;
        ShortcutService.OnRefreshRequested += HandleRefreshRequested;

        await InitializeSignalR();
        await LoadWatches();
        await Task.WhenAll(LoadRecentChanges(), LoadWatchGroups());
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Import keyboard shortcuts module for focusing elements
            try
            {
                _keyboardModule = await JSRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./js/keyboard-shortcuts.js");
            }
            catch (InvalidOperationException ex)
            {
                // Pre-rendering, ignore
                Console.WriteLine($"[Home] Error in OnAfterRenderAsync: {ex.Message}");
            }

            // Restore persisted filter state from localStorage
            var savedStatusFilter = await LocalStorage.GetItemAsync(LocalStorageKeys.HomeStatusFilter);
            var savedChangedRecently = await LocalStorage.GetItemAsync(LocalStorageKeys.HomeChangedRecently);
            var savedSearchText = await LocalStorage.GetItemAsync(LocalStorageKeys.HomeSearchText);

            var stateChanged = false;

            if (!string.IsNullOrEmpty(savedStatusFilter))
            {
                activeStatusFilter = savedStatusFilter;
                stateChanged = true;
            }

            if (!string.IsNullOrEmpty(savedChangedRecently) && bool.TryParse(savedChangedRecently, out var changedRecently) && changedRecently)
            {
                changedRecentlyFilter = true;
                activeStatusFilter = null; // changedRecentlyFilter takes precedence
                stateChanged = true;
            }

            if (!string.IsNullOrEmpty(savedSearchText))
            {
                searchText = savedSearchText;
                stateChanged = true;
            }

            // Load snooze state from localStorage
            await LoadSnoozeStateAsync();

            if (stateChanged || snoozedWatchIds.Any())
            {
                StateHasChanged();
            }
        }
    }

    private async Task InitializeSignalR()
    {
        var baseUri = Navigation.BaseUri.TrimEnd('/');
        hubConnection = new HubConnectionBuilder()
            .WithUrl($"{baseUri}/hubs/changes")
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            })
            .Build();

        hubConnection.On<object>("ChangeDetected", async (data) =>
        {
            await InvokeAsync(async () =>
            {
                await LoadRecentChanges();
                StateHasChanged();
            });
        });

        hubConnection.On<System.Text.Json.JsonElement>("WatchStatusChanged", async (data) =>
        {
            // Track processing events for Recent Activity
            var watchId = data.GetProperty("watchId").GetString() ?? data.GetProperty("watchId").GetGuid().ToString();
            var watchName = data.GetProperty("watchName").GetString() ?? "";
            var status = data.GetProperty("status").GetString() ?? "";

            if (status == "Checking")
            {
                // Watch is being checked - add to active processing
                activeProcessing[watchId] = new ProcessingActivity(watchId, watchName, DateTime.UtcNow);
            }
            else
            {
                // Check completed (success or error) - remove from active processing
                activeProcessing.Remove(watchId);
            }

            // Refresh watches to get updated status - must use InvokeAsync since we're on SignalR thread
            await InvokeAsync(async () =>
            {
                await LoadWatches();
                StateHasChanged();
            });
        });

        hubConnection.On<object>("WatchCreated", async (data) =>
        {
            await InvokeAsync(async () =>
            {
                try
                {
                    var response = await Http.GetAsync("/api/watches");
                    if (response.IsSuccessStatusCode)
                    {
                        watches = await response.Content.ReadFromJsonAsync<List<WatchListItemDto>>() ?? [];
                        InvalidateCache();
                    }
                }
                catch (Exception ex)
                {
                    /* Ignore load failures during SignalR events */
                    Console.WriteLine($"[Home] Error in InvokeAsync: {ex.Message}");
                }
                StateHasChanged();
            });
        });

        try
        {
            await hubConnection.StartAsync();
        }
        catch (Exception ex)
        {
            // SignalR connection failed - page will operate without real-time updates
            Console.WriteLine($"[Home] Error in InvokeAsync: {ex.Message}");
        }
    }

    private async Task LoadWatches()
    {
        isLoading = true;
        error = null;
        StateHasChanged();

        try
        {
            var response = await Http.GetAsync("/api/watches");
            if (response.IsSuccessStatusCode)
            {
                watches = await response.Content.ReadFromJsonAsync<List<WatchListItemDto>>() ?? [];
                InvalidateCache(); // Invalidate cache when watches change
            }
            else
            {
                error = "Failed to load watches.";
            }
        }
        catch (Exception ex)
        {
            error = $"Error loading watches: {ex.Message}";
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task LoadRecentChanges()
    {
        try
        {
            recentChanges = await Http.GetFromJsonAsync<List<ChangeListItemDto>>("/api/changes?limit=10") ?? [];
        }
        catch
        {
            recentChanges = [];
        }
    }

    private async Task LoadWatchGroups()
    {
        try
        {
            watchGroups = await Http.GetFromJsonAsync<List<WatchGroupListItemDto>>("/api/groups") ?? [];
        }
        catch
        {
            watchGroups = [];
        }
    }

    private async Task OnGroupExpanded(string groupId)
    {
        if (expandedGroupDetails.ContainsKey(groupId) && expandedGroupHealth.ContainsKey(groupId)) return;
        try
        {
            var detailTask = Http.GetFromJsonAsync<WatchGroupDetailDto>($"/api/groups/{groupId}");
            var healthTask = Http.GetFromJsonAsync<WatchGroupHealthDto>($"/api/groups/{groupId}/health");
            await Task.WhenAll(detailTask!, healthTask!);
            var detail = await detailTask!;
            var health = await healthTask!;
            if (detail != null)
            {
                expandedGroupDetails[groupId] = detail;
            }

            if (health != null)
            {
                expandedGroupHealth[groupId] = health;
            }

            StateHasChanged();
        }
        catch (Exception ex)
        {
            // Silent fail for group detail loading
            Console.WriteLine($"[Home] Error in OnGroupExpanded: {ex.Message}");
        }
    }

    // New Methods for Redesign
    private void GoToSettings() => Navigation.NavigateTo("/settings");

    private void ClearCreationMessage()
    {
        creationMessage = null;
        creationIsError = false;
    }

    private async Task RunAllChecks()
    {
        if (isCheckingAll) return;
        isCheckingAll = true;
        StateHasChanged();

        try
        {
            // Trigger check for all enabled watches
            var tasks = watches.Where(w => w.IsEnabled).Select(w => TriggerCheck(w.Id));
            await Task.WhenAll(tasks);

            // Poll for updates until all checks complete
            const int maxPolls = 60; // 5 minutes max (60 × 5s)
            for (var i = 0; i < maxPolls; i++)
            {
                await Task.Delay(5000);
                await LoadWatches();
                InvalidateCache();
                StateHasChanged();

                // Stop polling when no watches are still checking
                if (!watches.Any(w => w.Status == "Checking"))
                    break;
            }
        }
        finally
        {
            isCheckingAll = false;
            // Final refresh to ensure latest state
            await LoadWatches();
            InvalidateCache();
            StateHasChanged();
        }
    }

    private int GetChangesTodayCount() => recentChanges.Count(c => c.DetectedAt.Date == DateTime.Today);

    private string GetAvgCheckDuration() => "5 s"; // Mock

    private int GetActiveWatchesCount() => watches.Count(w => w.IsEnabled);

    private int GetSuccessRate()
    {
        if (!watches.Any()) return 0;
        var successCount = watches.Count(w => w.Status != "Error");
        return (int)((double)successCount / watches.Count * 100);
    }

    private async Task TriggerCheck(string id)
    {
        try
        {
            await Http.PostAsync($"/api/watches/{id}/check", null);
        }
        catch (Exception ex)
        {
            // Ignore errors for bulk check
            Console.WriteLine($"[Home] Error in TriggerCheck: {ex.Message}");
        }
    }

    private void NavigateToChange(string changeId)
    {
        Navigation.NavigateTo($"/changes/{changeId}");
    }

    private void NavigateToWatch(string watchId)
    {
        Navigation.NavigateTo($"/watches/{watchId}");
    }

    private static string GetImportanceIcon(string importance) => importance switch
    {
        "High" => "🔴",
        "Medium" => "🟡",
        _ => "🟢"
    };

    // ============================================================================
    // Activity Feed Grouping
    // ============================================================================

    /// <summary>
    /// Represents a group of changes for the same watch within a time bucket.
    /// </summary>
    private record ChangeGroup(string WatchId, string WatchTitle, List<ChangeListItemDto> Changes);

    /// <summary>
    /// Gets the available time buckets for the activity feed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Unsubscribe from keyboard shortcut events
        ShortcutService.OnFocusSearch -= HandleFocusSearch;
        ShortcutService.OnRefreshRequested -= HandleRefreshRequested;

        if (_keyboardModule is not null)
        {
            try
            {
                await _keyboardModule.DisposeAsync();
            }
            catch (JSDisconnectedException ex)
            {
                // Circuit disconnected, ignore
                Console.WriteLine($"[Home] Error in DisposeAsync: {ex.Message}");
            }
        }

        if (hubConnection is not null)
        {
            await hubConnection.DisposeAsync();
        }
    }
}
