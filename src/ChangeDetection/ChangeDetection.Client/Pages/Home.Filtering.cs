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
    private IReadOnlyList<WatchListItemDto> FilteredWatches
    {
        get
        {
            // Check if cache is valid
            if (_cachedFilteredWatches is not null &&
                _lastSearchText == searchText &&
                _lastStatusFilter == activeStatusFilter &&
                _lastChangedRecentlyFilter == changedRecentlyFilter &&
                _lastWatchesCount == watches.Count)
            {
                return _cachedFilteredWatches;
            }

            // Recompute and cache
            var query = watches.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                query = query.Where(w =>
                    (w.Title?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    w.Url.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    w.Tags.Any(t => t.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                );
            }

            if (changedRecentlyFilter)
            {
                query = query.Where(w => w.HasRecentChanges);
            }
            else if (activeStatusFilter == "Paused")
            {
                query = query.Where(w => !w.IsEnabled);
            }
            else if (activeStatusFilter == "Degraded")
            {
                query = query.Where(IsDegraded);
            }
            else if (activeStatusFilter == "SetupNeeded")
            {
                query = query.Where(NeedsSetup);
            }
            else if (activeStatusFilter == "Error")
            {
                query = query.Where(IsErrored);
            }
            else if (activeStatusFilter != null)
            {
                query = activeStatusFilter == "Active"
                    ? query.Where(IsHealthy)
                    : query.Where(w => w.Status == activeStatusFilter);
            }

            _cachedFilteredWatches = query.OrderByDescending(w => w.LastCheck).ToList();
            _lastSearchText = searchText;
            _lastStatusFilter = activeStatusFilter;
            _lastChangedRecentlyFilter = changedRecentlyFilter;
            _lastWatchesCount = watches.Count;

            return _cachedFilteredWatches;
        }
    }

    private void InvalidateCache()
    {
        _cachedFilteredWatches = null;
        _healthCountsValid = false;
    }

    // Health grid count methods with caching
    private void UpdateHealthCounts()
    {
        if (_healthCountsValid) return;

        int healthy = 0, checking = 0, error = 0, paused = 0, degraded = 0, setupNeeded = 0;

        foreach (var w in watches)
        {
            // Categories are MUTUALLY EXCLUSIVE - each watch counts in exactly ONE category
            if (NeedsSetup(w))
            {
                setupNeeded++;
            }
            else if (!w.IsEnabled)
            {
                // Disabled/Paused takes precedence
                paused++;
            }
            else if (w.Status == "Checking")
            {
                // Currently checking
                checking++;
            }
            else if (IsErrored(w))
            {
                error++;
            }
            else if (IsDegraded(w))
            {
                degraded++;
            }
            else
            {
                healthy++;
            }
        }

        _healthCounts = (healthy, checking, error, paused);
        _degradedCount = degraded;
        _setupNeededCount = setupNeeded;
        _healthCountsValid = true;
    }

    private bool IsHealthy(WatchListItemDto w)
        => !w.NeedsPipelineSetup
           && (string.Equals(w.HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase)
               || (string.IsNullOrWhiteSpace(w.HealthStatus)
                   && w.IsEnabled
                   && w.Status == "Active"
                   && !IsDegraded(w)
                   && !IsErrored(w)));

    private bool IsErrored(WatchListItemDto w)
        => !w.NeedsPipelineSetup
           && (string.Equals(w.HealthStatus, "Errored", StringComparison.OrdinalIgnoreCase)
               || (string.IsNullOrWhiteSpace(w.HealthStatus)
                   && (w.Status == "Error" || w.ExtractionQuality == "failed")));

    private bool IsDegraded(WatchListItemDto w)
        => !w.NeedsPipelineSetup
           && (string.Equals(w.HealthStatus, "Degraded", StringComparison.OrdinalIgnoreCase)
               || (string.IsNullOrWhiteSpace(w.HealthStatus)
                   && w.IsEnabled
                   && w.Status != "Error"
                   && w.Status != "Checking"
                   && w.LastCheck.HasValue
                   && w.LatestItemCount is 0));

    private static bool NeedsSetup(WatchListItemDto w)
        => w.NeedsPipelineSetup
           || string.Equals(w.HealthStatus, "SetupNeeded", StringComparison.OrdinalIgnoreCase);

    private static string GetHealthTitle(WatchListItemDto watch)
        => watch.HealthStatus switch
        {
            "Errored" => "Errored — latest check failed",
            "Degraded" => "Degraded — latest check needs attention",
            "SetupNeeded" => "Setup needed — configure this watch to start monitoring",
            _ => watch.Status
        };

    private int GetHealthyCount() { UpdateHealthCounts(); return _healthCounts.healthy; }

    private int GetCheckingCount() { UpdateHealthCounts(); return _healthCounts.checking; }

    private int GetErrorCount() { UpdateHealthCounts(); return _healthCounts.error; }

    private int GetPausedCount() { UpdateHealthCounts(); return _healthCounts.paused; }

    private int GetDegradedCount() { UpdateHealthCounts(); return _degradedCount; }

    private int GetSetupNeededCount() { UpdateHealthCounts(); return _setupNeededCount; }

    private bool IsHealthFilterActive(string? filter)
    {
        if (changedRecentlyFilter) return false;
        return activeStatusFilter == filter;
    }

    private void SetHealthFilter(string? filter)
    {
        activeStatusFilter = filter;
        changedRecentlyFilter = false;
        _ = PersistFiltersAsync();
    }

    private void SetFilter(string? status)
    {
        activeStatusFilter = status;
        changedRecentlyFilter = false;
        _ = PersistFiltersAsync();
    }

    private void ToggleChangedFilter()
    {
        changedRecentlyFilter = !changedRecentlyFilter;
        activeStatusFilter = null;
        _ = PersistFiltersAsync();
    }

    private async Task PersistFiltersAsync()
    {
        await LocalStorage.SetItemAsync(LocalStorageKeys.HomeStatusFilter, activeStatusFilter ?? "");
        await LocalStorage.SetItemAsync(LocalStorageKeys.HomeChangedRecently, changedRecentlyFilter.ToString());
    }

    private async Task PersistSearchTextAsync()
    {
        await LocalStorage.SetItemAsync(LocalStorageKeys.HomeSearchText, searchText);
    }

    private void HandleSearchBlur()
    {
        _ = PersistSearchTextAsync();
    }

    private void HandleFocusSearch()
    {
        _ = FocusSearchInputAsync();
    }

    private async Task FocusSearchInputAsync()
    {
        if (_keyboardModule is not null)
        {
            try
            {
                await _keyboardModule.InvokeVoidAsync("focusElement", "home-search-input");
            }
            catch (JSDisconnectedException ex)
            {
                // Circuit disconnected, ignore
                Console.WriteLine($"[Home] Error in FocusSearchInputAsync: {ex.Message}");
            }
        }
    }

    private void HandleRefreshRequested()
    {
        _ = RefreshWatchesAsync();
    }

    private async Task RefreshWatchesAsync()
    {
        await LoadWatches();
        await LoadRecentChanges();
        await InvokeAsync(StateHasChanged);
        ToastService.ShowSuccess("Watches refreshed");
    }

    private void NavigateToEdit(string watchId)
    {
        Navigation.NavigateTo($"/watches/{watchId}/edit");
    }

    // JS module reference for keyboard shortcuts
    private IJSObjectReference? _keyboardModule;

}
