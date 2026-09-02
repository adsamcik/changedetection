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
    private async Task LoadProviders()
    {
        isLoadingProviders = true;
        try
        {
            var response = await Http.GetAsync("/api/llm/providers");
            if (response.IsSuccessStatusCode)
            {
                providers = await response.Content.ReadFromJsonAsync<List<LlmProviderDto>>() ?? new();
            }
        }
        finally
        {
            isLoadingProviders = false;
        }
    }

    private async Task LoadUsageStats()
    {
        try
        {
            var response = await Http.GetAsync("/api/llm/usage");
            if (response.IsSuccessStatusCode)
            {
                usageStats = await response.Content.ReadFromJsonAsync<LlmUsageStatsDto>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error in LoadUsageStats: {ex.Message}");
        }
    }

    private void ShowAddProvider()
    {
        editingProvider = null;
        providerForm = new LlmProviderCreateDto
        {
            Priority = providers.Any() ? providers.Max(p => p.Priority) + 1 : 1,
            MaxTokens = 4096,
            IsEnabled = true
        };
        providerModalError = null;
        showProviderModal = true;
    }

    private void EditProvider(LlmProviderDto provider)
    {
        editingProvider = provider;
        providerForm = new LlmProviderCreateDto
        {
            ProviderType = provider.ProviderType,
            ApiKey = "", // Don't show existing key
            Endpoint = provider.Endpoint,
            DeploymentName = provider.DeploymentName,
            ModelId = provider.ModelId,
            Priority = provider.Priority,
            MaxTokens = provider.MaxTokens,
            CostPerInputToken = provider.CostPerInputToken,
            CostPerOutputToken = provider.CostPerOutputToken,
            IsEnabled = provider.IsEnabled
        };
        providerModalError = null;
        showProviderModal = true;
    }

    private void CloseProviderModal()
    {
        showProviderModal = false;
        editingProvider = null;
    }

    private async Task SaveProvider()
    {
        isSavingProvider = true;
        providerModalError = null;

        try
        {
            HttpResponseMessage response;
            if (editingProvider != null)
            {
                response = await Http.PutAsJsonAsync($"/api/llm/providers/{editingProvider.Id}", providerForm);
            }
            else
            {
                response = await Http.PostAsJsonAsync("/api/llm/providers", providerForm);
            }

            if (response.IsSuccessStatusCode)
            {
                CloseProviderModal();
                await LoadProviders();
            }
            else
            {
                providerModalError = await response.Content.ReadAsStringAsync();
            }
        }
        catch (Exception ex)
        {
            providerModalError = ex.Message;
        }
        finally
        {
            isSavingProvider = false;
        }
    }

    private async Task ToggleProvider(LlmProviderDto provider)
    {
        var endpoint = provider.IsEnabled
            ? $"/api/llm/providers/{provider.Id}/disable"
            : $"/api/llm/providers/{provider.Id}/enable";
        await Http.PostAsync(endpoint, null);
        await LoadProviders();
    }

    private async Task MoveProvider(LlmProviderDto provider, int direction)
    {
        var newPriority = provider.Priority + direction;
        if (newPriority < 1) return;

        // Swap priorities with adjacent provider
        var adjacent = providers.FirstOrDefault(p => p.Priority == newPriority);
        if (adjacent != null)
        {
            var updateForm = new LlmProviderCreateDto { Priority = provider.Priority };
            await Http.PutAsJsonAsync($"/api/llm/providers/{adjacent.Id}", updateForm);
        }

        var form = new LlmProviderCreateDto { Priority = newPriority };
        await Http.PutAsJsonAsync($"/api/llm/providers/{provider.Id}", form);
        await LoadProviders();
    }

    private async Task DeleteProvider(LlmProviderDto provider)
    {
        await Http.DeleteAsync($"/api/llm/providers/{provider.Id}");
        await LoadProviders();
    }

    private string GetModelPlaceholder(string? providerType) => providerType switch
    {
        "OpenAI" => "gpt-4o, gpt-4o-mini",
        "AzureOpenAI" => "gpt-4",
        "Ollama" => "llama3.1, mistral",
        "Gemini" => "gemini-1.5-pro, gemini-1.5-flash",
        "Claude" => "claude-3-5-sonnet-20241022",
        _ => "model-name"
    };

}
