using System.Net;
using System.Net.Http.Json;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.UITests.Helpers;

/// <summary>
/// Seeds and manages test data via the REST API.
/// Uses direct HTTP calls to create watches, changes, etc. for test scenarios.
/// Includes retry logic for rate-limited requests.
/// </summary>
public class WatchApiHelper(HttpClient client)
{
    private const int MaxRetries = 3;

    /// <summary>
    /// Creates a watch and returns its detail DTO.
    /// </summary>
    public async Task<WatchDetailDto> CreateWatchAsync(string url, string? title = null)
    {
        var request = new WatchCreateDto
        {
            Url = url,
            Title = title ?? $"Test Watch - {url}",
            CheckInterval = TimeSpan.FromHours(1)
        };

        var response = await SendWithRetryAsync(() =>
            client.PostAsJsonAsync("/api/watches", request));
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<WatchDetailDto>();
        return result ?? throw new InvalidOperationException("Failed to deserialize created watch");
    }

    /// <summary>
    /// Gets all watches.
    /// </summary>
    public async Task<List<WatchListItemDto>> GetWatchesAsync()
    {
        var response = await SendWithRetryAsync(() => client.GetAsync("/api/watches"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<WatchListItemDto>>();
        return result ?? [];
    }

    /// <summary>
    /// Deletes a watch by ID.
    /// </summary>
    public async Task DeleteWatchAsync(string watchId)
    {
        var response = await SendWithRetryAsync(() =>
            client.DeleteAsync($"/api/watches/{watchId}"));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sends an HTTP request with retry on 429 TooManyRequests.
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> sendFunc)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await sendFunc();

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;

            // Wait before retrying — use Retry-After header or exponential backoff
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
            await Task.Delay(retryAfter);
        }

        // Final attempt without retry
        return await sendFunc();
    }
}
