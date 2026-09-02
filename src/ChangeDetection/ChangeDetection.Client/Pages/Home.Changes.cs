using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using ChangeDetection.Client.Components;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Client.Pages;

public partial class Home
{
    private static IEnumerable<string> GetTimeBuckets() =>
    [
        "Just now",
        "Last hour",
        "Today",
        "Yesterday",
        "This week",
        "Older"
    ];

    /// <summary>
    /// Gets the duration description for a time bucket.
    /// </summary>
    private static string GetBucketDuration(string bucket) => bucket switch
    {
        "Just now" => "5 minutes",
        "Last hour" => "hour",
        "Today" => "day",
        "Yesterday" => "day",
        "This week" => "week",
        _ => "period"
    };

    /// <summary>
    /// Determines which time bucket a change belongs to.
    /// Uses local time for date-based comparisons since "Today" and "Yesterday" are local concepts.
    /// </summary>
    private static string GetTimeBucket(DateTime utcTime)
    {
        var now = DateTime.Now;  // Local time
        var localTime = utcTime.ToLocalTime();  // Convert UTC to local
        var diff = now - localTime;

        if (diff.TotalMinutes < 5) return "Just now";
        if (diff.TotalHours < 1) return "Last hour";

        // Use local dates for day comparisons
        if (localTime.Date == now.Date) return "Today";
        if (localTime.Date == now.Date.AddDays(-1)) return "Yesterday";

        if (diff.TotalDays < 7) return "This week";
        return "Older";
    }

    /// <summary>
    /// Groups changes by watch within a time bucket.
    /// </summary>
    private IEnumerable<ChangeGroup> GetGroupedChangesForBucket(string bucket)
    {
        // Filter changes for this bucket, excluding snoozed watches
        var bucketChanges = recentChanges
            .Where(c => GetTimeBucket(c.DetectedAt) == bucket)
            .Where(c => !IsWatchSnoozed(c.WatchId))
            .ToList();

        // Group by watch
        return bucketChanges
            .GroupBy(c => c.WatchId)
            .Select(g => new ChangeGroup(
                g.Key,
                g.First().WatchTitle ?? "Unknown",
                g.OrderByDescending(c => c.DetectedAt).ToList()))
            .OrderByDescending(g => g.Changes.Max(c => c.DetectedAt));
    }

    /// <summary>
    /// Checks if a watch is currently snoozed.
    /// </summary>
    private bool IsWatchSnoozed(string watchId)
    {
        if (!snoozeExpirations.TryGetValue(watchId, out var expiration))
            return false;

        if (DateTime.UtcNow >= expiration)
        {
            snoozeExpirations.Remove(watchId);
            snoozedWatchIds.Remove(watchId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the CSS class based on importance level.
    /// </summary>
    private static string GetImportanceClass(string importance) => importance switch
    {
        "High" => "importance-high-item",
        "Medium" => "importance-medium-item",
        _ => ""
    };

    /// <summary>
    /// Gets the numeric order for importance (for sorting).
    /// </summary>
    private static int GetImportanceOrder(string importance) => importance switch
    {
        "High" => 3,
        "Medium" => 2,
        _ => 1
    };

    /// <summary>
    /// Toggles the expanded state of a change group.
    /// </summary>
    private void ToggleGroupExpand(string watchId)
    {
        if (!expandedGroups.Add(watchId))
        {
            expandedGroups.Remove(watchId);
        }
    }

    /// <summary>
    /// Marks a single change as read.
    /// </summary>
    private async Task MarkAsRead(string changeId)
    {
        try
        {
            await Http.PostAsync($"/api/changes/{changeId}/view", null);
            var change = recentChanges.FirstOrDefault(c => c.Id == changeId);
            if (change != null)
            {
                change.IsViewed = true;
            }
            StateHasChanged();
        }
        catch (Exception ex)
        {
            // Silently fail - not critical
            Console.WriteLine($"[Home] Error in MarkAsRead: {ex.Message}");
        }
    }

    /// <summary>
    /// Marks all changes for a watch as read.
    /// </summary>
    private async Task MarkGroupAsRead(string watchId)
    {
        var changes = recentChanges.Where(c => c.WatchId == watchId && !c.IsViewed).ToList();
        foreach (var change in changes)
        {
            try
            {
                await Http.PostAsync($"/api/changes/{change.Id}/view", null);
                change.IsViewed = true;
            }
            catch (Exception ex)
            {
                // Continue with next
                Console.WriteLine($"[Home] Error in MarkGroupAsRead: {ex.Message}");
            }
        }
        StateHasChanged();
    }

    /// <summary>
    /// Snoozes a watch from the activity feed for the specified number of hours.
    /// </summary>
    private async Task SnoozeWatch(string watchId, int hours)
    {
        snoozedWatchIds.Add(watchId);
        snoozeExpirations[watchId] = DateTime.UtcNow.AddHours(hours);
        await SaveSnoozeStateAsync();
        ToastService.ShowInfo($"Snoozed for {hours} hour(s)");
        StateHasChanged();
    }

    /// <summary>
    /// Un-snoozes a watch, resuming activity feed notifications.
    /// </summary>
    private async Task UnsnoozeWatch(string watchId)
    {
        snoozedWatchIds.Remove(watchId);
        snoozeExpirations.Remove(watchId);
        await SaveSnoozeStateAsync();
        ToastService.ShowInfo("Watch un-snoozed");
        StateHasChanged();
    }

    /// <summary>
    /// Saves snooze state to localStorage.
    /// </summary>
    private async Task SaveSnoozeStateAsync()
    {
        var snoozeData = snoozeExpirations
            .Where(kvp => kvp.Value > DateTime.UtcNow)  // Only save unexpired
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString("o"));

        await LocalStorage.SetItemAsync(SnoozeStorageKey, JsonSerializer.Serialize(snoozeData));
    }

    /// <summary>
    /// Loads snooze state from localStorage.
    /// </summary>
    private async Task LoadSnoozeStateAsync()
    {
        try
        {
            var json = await LocalStorage.GetItemAsync(SnoozeStorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var snoozeData = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (snoozeData != null)
                {
                    foreach (var kvp in snoozeData)
                    {
                        if (DateTime.TryParse(kvp.Value, out var expiration) && expiration > DateTime.UtcNow)
                        {
                            snoozedWatchIds.Add(kvp.Key);
                            snoozeExpirations[kvp.Key] = expiration;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            /* Ignore corrupt data */
            Console.WriteLine($"[Home] Error in LoadSnoozeStateAsync: {ex.Message}");
        }
    }

    private static string TruncateSummary(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }

    private static string FormatTimeAgo(DateTime dateTime)
    {
        var diff = DateTime.UtcNow - dateTime;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }

    private string FormatSnoozeExpiration(string watchId)
    {
        if (!snoozeExpirations.TryGetValue(watchId, out var expiration))
            return "unknown";

        var diff = expiration - DateTime.UtcNow;
        if (diff.TotalMinutes < 1) return "< 1m";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h";
        return $"{(int)diff.TotalDays}d";
    }

    private string GetDisplayTitle(WatchListItemDto watch)
    {
        if (!string.IsNullOrEmpty(watch.Title))
            return watch.Title;

        try
        {
            var uri = new Uri(watch.Url);
            return uri.Host;
        }
        catch
        {
            return watch.Url;
        }
    }

    private static string TruncateUrl(string url)
    {
        if (url.Length <= 50) return url;
        return url[..47] + "...";
    }

    // ============================================================================
    // Bulk Operations
    // ============================================================================

}
