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
    private async Task LoadLogs()
    {
        isLoadingLogs = true;
        StateHasChanged();
        try
        {
            var response = await Http.GetAsync("/api/llm/logs?count=200");
            if (response.IsSuccessStatusCode)
            {
                var logsResponse = await response.Content.ReadFromJsonAsync<LlmLogsResponse>();
                llmLogs = logsResponse?.Logs ?? new();
            }
        }
                catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error in LoadLogs: {ex.Message}");
        }
        finally
        {
            isLoadingLogs = false;
            StateHasChanged();
        }
    }

    private async Task RefreshLogs()
    {
        await LoadLogs();
    }

    private async Task ClearLogs()
    {
        await Http.DeleteAsync("/api/llm/logs");
        llmLogs.Clear();
        StateHasChanged();
    }

    private static string GetLogLevelClass(string level) => level switch
    {
        "Error" => "level-error",
        "Warning" => "level-warning",
        "Info" => "level-info",
        "Debug" => "level-debug",
        _ => ""
    };

}
