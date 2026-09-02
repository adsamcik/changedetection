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
    private void ToggleBulkMode()
    {
        isBulkMode = !isBulkMode;
        if (!isBulkMode)
        {
            selectedWatchIds.Clear();
            ClearBulkMessage();
        }
    }

    private void OnRowClick(string watchId)
    {
        if (isBulkMode)
        {
            ToggleWatchSelection(watchId);
        }
        else
        {
            NavigateToWatch(watchId);
        }
    }

    private void ToggleWatchSelection(string watchId)
    {
        if (!selectedWatchIds.Add(watchId))
        {
            selectedWatchIds.Remove(watchId);
        }
    }

    private void ToggleSelectAll()
    {
        if (IsAllSelected)
        {
            selectedWatchIds.Clear();
        }
        else
        {
            foreach (var watch in FilteredWatches)
            {
                selectedWatchIds.Add(watch.Id);
            }
        }
    }

    private void ClearBulkMessage()
    {
        bulkOperationMessage = null;
        bulkOperationIsError = false;
    }

    private async Task BulkEnable()
    {
        if (selectedWatchIds.Count == 0) return;

        isBulkOperating = true;
        ClearBulkMessage();
        StateHasChanged();

        try
        {
            var response = await Http.PostAsJsonAsync("/api/watches/bulk/enable", new BulkWatchOperationDto
            {
                WatchIds = selectedWatchIds.ToList()
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<BulkOperationResultDto>();
                var message = $"Enabled {result?.SuccessCount ?? 0} watches";
                if (result?.FailureCount > 0)
                {
                    message += $" ({result.FailureCount} failed)";
                    ToastService.ShowWarning(message);
                }
                else
                {
                    ToastService.ShowSuccess(message);
                }
                await LoadWatches();
                selectedWatchIds.Clear();
            }
            else
            {
                ToastService.ShowError("Failed to enable watches");
            }
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error: {ex.Message}");
        }
        finally
        {
            isBulkOperating = false;
        }
    }

    private async Task BulkDisable()
    {
        if (selectedWatchIds.Count == 0) return;

        isBulkOperating = true;
        ClearBulkMessage();
        StateHasChanged();

        try
        {
            var response = await Http.PostAsJsonAsync("/api/watches/bulk/disable", new BulkWatchOperationDto
            {
                WatchIds = selectedWatchIds.ToList()
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<BulkOperationResultDto>();
                var message = $"Disabled {result?.SuccessCount ?? 0} watches";
                if (result?.FailureCount > 0)
                {
                    message += $" ({result.FailureCount} failed)";
                    ToastService.ShowWarning(message);
                }
                else
                {
                    ToastService.ShowSuccess(message);
                }
                await LoadWatches();
                selectedWatchIds.Clear();
            }
            else
            {
                ToastService.ShowError("Failed to disable watches");
            }
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error: {ex.Message}");
        }
        finally
        {
            isBulkOperating = false;
        }
    }

    private async Task BulkCheck()
    {
        if (selectedWatchIds.Count == 0) return;

        isBulkOperating = true;
        ClearBulkMessage();
        StateHasChanged();

        try
        {
            var response = await Http.PostAsJsonAsync("/api/watches/bulk/check", new BulkWatchOperationDto
            {
                WatchIds = selectedWatchIds.ToList()
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<BulkOperationResultDto>();
                var message = $"Triggered check for {result?.SuccessCount ?? 0} watches";
                if (result?.FailureCount > 0)
                {
                    message += $" ({result.FailureCount} failed)";
                    ToastService.ShowWarning(message);
                }
                else
                {
                    ToastService.ShowSuccess(message);
                }
                selectedWatchIds.Clear();
            }
            else
            {
                ToastService.ShowError("Failed to trigger checks");
            }
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error: {ex.Message}");
        }
        finally
        {
            isBulkOperating = false;
        }
    }

    private void BulkDelete()
    {
        if (selectedWatchIds.Count == 0) return;
        showBulkDeleteConfirm = true;
    }

    private string GetBulkDeleteMessage()
    {
        var count = selectedWatchIds.Count;
        return count == 1
            ? "Are you sure you want to delete this watch? This action cannot be undone."
            : $"Are you sure you want to delete {count} watches? This action cannot be undone.";
    }

    private void CancelBulkDelete()
    {
        showBulkDeleteConfirm = false;
    }

    private async Task ConfirmBulkDelete()
    {
        isBulkOperating = true;
        ClearBulkMessage();
        StateHasChanged();

        try
        {
            var response = await Http.PostAsJsonAsync("/api/watches/bulk/delete", new BulkWatchOperationDto
            {
                WatchIds = selectedWatchIds.ToList()
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<BulkOperationResultDto>();
                var message = $"Deleted {result?.SuccessCount ?? 0} watches";
                if (result?.FailureCount > 0)
                {
                    message += $" ({result.FailureCount} failed)";
                    ToastService.ShowWarning(message);
                }
                else
                {
                    ToastService.ShowSuccess(message);
                }
                await LoadWatches();
                selectedWatchIds.Clear();
            }
            else
            {
                ToastService.ShowError("Failed to delete watches");
            }
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error: {ex.Message}");
        }
        finally
        {
            isBulkOperating = false;
            showBulkDeleteConfirm = false;
        }
    }

    private void OpenBulkEditDialog()
    {
        bulkEditModel = new BulkEditModel();
        showBulkEditDialog = true;
    }

    private void CloseBulkEditDialog()
    {
        showBulkEditDialog = false;
    }

    private async Task ApplyBulkEdit()
    {
        if (selectedWatchIds.Count == 0) return;

        isBulkOperating = true;
        ClearBulkMessage();
        StateHasChanged();

        try
        {
            var dto = new BulkWatchEditDto
            {
                WatchIds = selectedWatchIds.ToList(),
                AddTags = string.IsNullOrWhiteSpace(bulkEditModel.AddTags)
                    ? null
                    : bulkEditModel.AddTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                RemoveTags = string.IsNullOrWhiteSpace(bulkEditModel.RemoveTags)
                    ? null
                    : bulkEditModel.RemoveTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                ChangeCategoryId = bulkEditModel.ChangeCategoryId,
                CategoryId = bulkEditModel.ChangeCategoryId ? bulkEditModel.CategoryId : null,
                CheckInterval = bulkEditModel.ChangeCheckInterval ? bulkEditModel.CheckInterval : null,
                UseJavaScript = bulkEditModel.ChangeUseJavaScript ? bulkEditModel.UseJavaScript : null,
                NotificationsEnabled = bulkEditModel.ChangeNotifications ? bulkEditModel.NotificationsEnabled : null
            };

            var response = await Http.PostAsJsonAsync("/api/watches/bulk/edit", dto);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<BulkOperationResultDto>();
                var message = $"Updated {result?.SuccessCount ?? 0} watches";
                if (result?.FailureCount > 0)
                {
                    message += $" ({result.FailureCount} failed)";
                    ToastService.ShowWarning(message);
                }
                else
                {
                    ToastService.ShowSuccess(message);
                }
                await LoadWatches();
                selectedWatchIds.Clear();
                showBulkEditDialog = false;
            }
            else
            {
                ToastService.ShowError("Failed to update watches");
            }
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error: {ex.Message}");
        }
        finally
        {
            isBulkOperating = false;
        }
    }

    private class BulkEditModel
    {
        public string? AddTags { get; set; }
        public string? RemoveTags { get; set; }
        public bool ChangeCategoryId { get; set; }
        public string? CategoryId { get; set; }
        public bool ChangeCheckInterval { get; set; }
        public TimeSpan? CheckInterval { get; set; }
        public bool ChangeUseJavaScript { get; set; }
        public bool? UseJavaScript { get; set; }
        public bool ChangeNotifications { get; set; }
        public bool? NotificationsEnabled { get; set; }
    }

}
