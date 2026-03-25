using Microsoft.JSInterop;

namespace ChangeDetection.Client;

/// <summary>
/// Represents the user's theme preference mode.
/// </summary>
public enum ThemeMode
{
    /// <summary>Follow system preference (prefers-color-scheme).</summary>
    System,
    /// <summary>Always use light theme.</summary>
    Light,
    /// <summary>Always use dark theme.</summary>
    Dark
}

/// <summary>
/// Service for managing theme preferences with localStorage persistence
/// and system theme detection support.
/// </summary>
public sealed class ThemeService(LocalStorageService localStorage, IJSRuntime jsRuntime) : IAsyncDisposable
{
    private const string StorageKey = "changedetection_theme";
    private IJSObjectReference? _module;
    private DotNetObjectReference<ThemeService>? _dotNetRef;
    private bool _disposed;

    /// <summary>
    /// Event raised when the theme changes (either by user action or system preference change).
    /// </summary>
    public event Action? OnThemeChanged;

    /// <summary>
    /// Gets the current user-selected theme mode.
    /// </summary>
    public ThemeMode CurrentMode { get; private set; } = ThemeMode.System;

    /// <summary>
    /// Gets the effective theme ("light" or "dark") based on current mode and system preference.
    /// </summary>
    public string EffectiveTheme { get; private set; } = "light";

    /// <summary>
    /// Initializes the theme service by loading saved preference and setting up system theme watching.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_disposed) return;

        try
        {
            // Load the JS module
            _module = await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./js/theme-toggle.js");

            // Load saved theme preference
            var savedMode = await localStorage.GetItemAsync<string>(StorageKey);
            if (!string.IsNullOrEmpty(savedMode) && Enum.TryParse<ThemeMode>(savedMode, out var mode))
            {
                CurrentMode = mode;
            }

            // Set up system theme watching
            _dotNetRef = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("watchSystemTheme", _dotNetRef);

            // Apply the initial theme
            await ApplyThemeAsync();
        }
                catch (JSDisconnectedException ex)
        {
            // Ignore - circuit disconnected
            Console.WriteLine($"[ThemeService] Error in ApplyThemeAsync: {ex.Message}");
        }
                catch (InvalidOperationException ex)
        {
            // Ignore - prerendering or circuit not available
            Console.WriteLine($"[ThemeService] Error in ApplyThemeAsync: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the theme mode and persists it to localStorage.
    /// </summary>
    /// <param name="mode">The theme mode to set.</param>
    public async Task SetThemeAsync(ThemeMode mode)
    {
        if (_disposed) return;

        CurrentMode = mode;
        await localStorage.SetItemAsync(StorageKey, mode.ToString());
        await ApplyThemeAsync();
        OnThemeChanged?.Invoke();
    }

    /// <summary>
    /// Cycles to the next theme mode: System → Light → Dark → System.
    /// </summary>
    public async Task CycleThemeAsync()
    {
        var nextMode = CurrentMode switch
        {
            ThemeMode.System => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            ThemeMode.Dark => ThemeMode.System,
            _ => ThemeMode.System
        };
        await SetThemeAsync(nextMode);
    }

    /// <summary>
    /// Gets the effective theme based on current mode and system preference.
    /// </summary>
    /// <returns>"light" or "dark"</returns>
    public async Task<string> GetEffectiveThemeAsync()
    {
        if (_module is null || _disposed) return "light";

        try
        {
            if (CurrentMode == ThemeMode.Light) return "light";
            if (CurrentMode == ThemeMode.Dark) return "dark";

            // System mode - get system preference
            return await _module.InvokeAsync<string>("getSystemTheme");
        }
        catch (JSDisconnectedException)
        {
            return "light";
        }
    }

    /// <summary>
    /// Called from JavaScript when the system theme changes.
    /// </summary>
    [JSInvokable]
    public async Task OnSystemThemeChanged(string systemTheme)
    {
        if (_disposed) return;

        // Only update if we're in System mode
        if (CurrentMode == ThemeMode.System)
        {
            EffectiveTheme = systemTheme;
            await ApplyThemeAsync();
            OnThemeChanged?.Invoke();
        }
    }

    private async Task ApplyThemeAsync()
    {
        if (_module is null || _disposed) return;

        try
        {
            EffectiveTheme = await GetEffectiveThemeAsync();
            await _module.InvokeVoidAsync("setThemeAttribute", EffectiveTheme);
        }
                catch (JSDisconnectedException ex)
        {
            // Ignore - circuit disconnected
            Console.WriteLine($"[ThemeService] Error in ApplyThemeAsync: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _dotNetRef?.Dispose();

        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("stopWatchingSystemTheme");
                await _module.DisposeAsync();
            }
                        catch (JSDisconnectedException ex)
            {
                // Ignore - circuit disconnected
                Console.WriteLine($"[ThemeService] Error in DisposeAsync: {ex.Message}");
            }
        }
    }
}
