using Microsoft.JSInterop;

namespace ChangeDetection.Client;

/// <summary>
/// Service that manages global keyboard shortcuts for the application.
/// Listens for keyboard events and dispatches registered shortcut handlers.
/// </summary>
public sealed class KeyboardShortcutService : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;
    private DotNetObjectReference<KeyboardShortcutService>? _dotNetRef;
    private bool _isInitialized;

    /// <summary>
    /// Event fired when a keyboard shortcut is triggered.
    /// </summary>
    public event Action<KeyboardShortcut>? OnShortcutTriggered;

    /// <summary>
    /// Event fired when the shortcuts help modal should be shown.
    /// </summary>
    public event Action? OnShowHelp;

    /// <summary>
    /// Event fired when Escape is pressed (to close modals/dialogs).
    /// </summary>
    public event Action? OnEscapePressed;

    /// <summary>
    /// Event fired when the search should be focused.
    /// </summary>
    public event Action? OnFocusSearch;

    /// <summary>
    /// Event fired when refresh is requested.
    /// </summary>
    public event Action? OnRefreshRequested;

    /// <summary>
    /// Event fired when new watch should be created.
    /// </summary>
    public event Action? OnNewWatch;

    public KeyboardShortcutService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    /// <summary>
    /// Initializes the keyboard shortcut listener.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "/js/keyboard-shortcuts.js");
            
            _dotNetRef = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("initialize", _dotNetRef);
            _isInitialized = true;
        }
                catch (InvalidOperationException ex)
        {
            // Pre-rendering or server-side without JS available
            Console.WriteLine($"[KeyboardShortcutService] Error in InitializeAsync: {ex.Message}");
        }
    }

    /// <summary>
    /// Called from JavaScript when a keyboard shortcut is detected.
    /// </summary>
    [JSInvokable]
    public void HandleKeyDown(string key, bool ctrlKey, bool altKey, bool shiftKey, string tagName, bool isInput)
    {
        // Don't handle shortcuts when typing in input fields (except for Escape)
        if (isInput && key != "Escape")
        {
            return;
        }

        var shortcut = new KeyboardShortcut(key, ctrlKey, altKey, shiftKey);

        // Handle specific shortcuts
        if (key == "Escape")
        {
            OnEscapePressed?.Invoke();
            return;
        }

        if (key == "?" && !ctrlKey && !altKey && !shiftKey)
        {
            OnShowHelp?.Invoke();
            return;
        }

        if ((key == "/" || (key == "k" && ctrlKey)) && !altKey && !shiftKey)
        {
            OnFocusSearch?.Invoke();
            return;
        }

        if (key == "r" && !ctrlKey && !altKey && !shiftKey)
        {
            OnRefreshRequested?.Invoke();
            return;
        }

        if (key == "n" && !ctrlKey && !altKey && !shiftKey)
        {
            OnNewWatch?.Invoke();
            return;
        }

        // Fire general shortcut event for custom handling
        OnShortcutTriggered?.Invoke(shortcut);
    }

    public async ValueTask DisposeAsync()
    {
        _isInitialized = false;
        
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose");
            }
                        catch (JSDisconnectedException ex)
            {
                // Circuit disconnected, ignore
                Console.WriteLine($"[KeyboardShortcutService] Error in DisposeAsync: {ex.Message}");
            }
                        catch (ObjectDisposedException ex)
            {
                // Already disposed, ignore
                Console.WriteLine($"[KeyboardShortcutService] Error in DisposeAsync: {ex.Message}");
            }
            
            try
            {
                await _module.DisposeAsync();
            }
                        catch (JSDisconnectedException ex)
            {
                // Circuit disconnected, ignore
                Console.WriteLine($"[KeyboardShortcutService] Error in DisposeAsync: {ex.Message}");
            }
        }

        _dotNetRef?.Dispose();
        _dotNetRef = null;
        _module = null;
    }
}

/// <summary>
/// Represents a keyboard shortcut combination.
/// </summary>
public record KeyboardShortcut(string Key, bool CtrlKey, bool AltKey, bool ShiftKey)
{
    /// <summary>
    /// Gets a display-friendly string for this shortcut.
    /// </summary>
    public string DisplayString
    {
        get
        {
            var parts = new List<string>();
            if (CtrlKey) parts.Add("Ctrl");
            if (AltKey) parts.Add("Alt");
            if (ShiftKey) parts.Add("Shift");
            parts.Add(Key.Length == 1 ? Key.ToUpperInvariant() : Key);
            return string.Join(" + ", parts);
        }
    }
}
