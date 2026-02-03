using System.Text.Json;
using Microsoft.JSInterop;

namespace ChangeDetection.Client;

/// <summary>
/// Well-known localStorage keys used across the application.
/// Centralized to prevent key collisions and enable discoverability.
/// </summary>
public static class LocalStorageKeys
{
    private const string Prefix = "changedetection_";
    
    public const string SettingsTab = $"{Prefix}settings_tab";
    public const string HomeStatusFilter = $"{Prefix}home_status_filter";
    public const string HomeSearchText = $"{Prefix}home_search_text";
    public const string HomeChangedRecently = $"{Prefix}home_changed_recently";
}

/// <summary>
/// Result of a localStorage operation, indicating success or the type of failure.
/// </summary>
public enum StorageResult
{
    Success,
    NotInBrowser,
    QuotaExceeded,
    StorageDisabled,
    Error
}

/// <summary>
/// Service for accessing browser localStorage with safe handling for server-side rendering.
/// Implements IAsyncDisposable to properly clean up JS module references.
/// </summary>
public sealed class LocalStorageService(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _isInitialized;
    private bool _isBrowser;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;

        await _initLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_isInitialized) return;

            try
            {
                // Check if we're running in a browser context
                // IJSInProcessRuntime is only available in WebAssembly
                // For server-side, we use regular JSRuntime which will work after render
                _module = await jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "/localStorage.js");
                _isBrowser = true;
            }
            catch (InvalidOperationException)
            {
                // Pre-rendering or server-side without JS available
                _isBrowser = false;
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected
                _isBrowser = false;
            }

            _isInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Gets a value from localStorage.
    /// </summary>
    /// <param name="key">The key to retrieve.</param>
    /// <returns>The stored value, or null if not found or not in browser context.</returns>
    public async Task<string?> GetItemAsync(string key)
    {
        if (_disposed) return null;
        
        await EnsureInitializedAsync();
        
        if (!_isBrowser || _module is null) return null;

        try
        {
            return await _module.InvokeAsync<string?>("getItem", key);
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets and deserializes a value from localStorage.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="key">The key to retrieve.</param>
    /// <returns>The deserialized value, or default if not found or deserialization fails.</returns>
    public async Task<T?> GetItemAsync<T>(string key)
    {
        var json = await GetItemAsync(key);
        if (string.IsNullOrEmpty(json)) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Sets a value in localStorage.
    /// </summary>
    /// <param name="key">The key to set.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>Result indicating success or failure type.</returns>
    public async Task<StorageResult> SetItemAsync(string key, string value)
    {
        if (_disposed) return StorageResult.NotInBrowser;
        
        await EnsureInitializedAsync();
        
        if (!_isBrowser || _module is null) return StorageResult.NotInBrowser;

        try
        {
            var success = await _module.InvokeAsync<bool>("setItem", key, value);
            return success ? StorageResult.Success : StorageResult.QuotaExceeded;
        }
        catch (JSDisconnectedException)
        {
            return StorageResult.NotInBrowser;
        }
        catch (JSException ex) when (ex.Message.Contains("QuotaExceeded", StringComparison.OrdinalIgnoreCase))
        {
            return StorageResult.QuotaExceeded;
        }
        catch
        {
            return StorageResult.Error;
        }
    }

    /// <summary>
    /// Serializes and sets a value in localStorage.
    /// </summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="key">The key to set.</param>
    /// <param name="value">The value to serialize and store.</param>
    /// <returns>Result indicating success or failure type.</returns>
    public async Task<StorageResult> SetItemAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return await SetItemAsync(key, json);
    }

    /// <summary>
    /// Removes a value from localStorage.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>Result indicating success or failure type.</returns>
    public async Task<StorageResult> RemoveItemAsync(string key)
    {
        if (_disposed) return StorageResult.NotInBrowser;
        
        await EnsureInitializedAsync();
        
        if (!_isBrowser || _module is null) return StorageResult.NotInBrowser;

        try
        {
            await _module.InvokeAsync<bool>("removeItem", key);
            return StorageResult.Success;
        }
        catch (JSDisconnectedException)
        {
            return StorageResult.NotInBrowser;
        }
        catch
        {
            return StorageResult.Error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already disconnected, ignore
            }
        }

        _initLock.Dispose();
    }
}
