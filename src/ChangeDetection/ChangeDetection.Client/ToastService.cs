namespace ChangeDetection.Client;

/// <summary>
/// A service to manage toast notifications across the application.
/// Components can subscribe to toast events and display them in the UI.
/// </summary>
public sealed class ToastService
{
    /// <summary>
    /// Event raised when a toast notification should be displayed.
    /// </summary>
    public event Action<ToastMessage>? OnShow;

    /// <summary>
    /// Shows a success toast notification.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="duration">Optional duration in milliseconds. Defaults to 5000ms.</param>
    public void ShowSuccess(string message, int? duration = null)
        => Show(new ToastMessage(message, ToastType.Success, duration));

    /// <summary>
    /// Shows an error toast notification.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="duration">Optional duration in milliseconds. Defaults to 5000ms.</param>
    public void ShowError(string message, int? duration = null)
        => Show(new ToastMessage(message, ToastType.Error, duration));

    /// <summary>
    /// Shows a warning toast notification.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="duration">Optional duration in milliseconds. Defaults to 5000ms.</param>
    public void ShowWarning(string message, int? duration = null)
        => Show(new ToastMessage(message, ToastType.Warning, duration));

    /// <summary>
    /// Shows an info toast notification.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="duration">Optional duration in milliseconds. Defaults to 5000ms.</param>
    public void ShowInfo(string message, int? duration = null)
        => Show(new ToastMessage(message, ToastType.Info, duration));

    private void Show(ToastMessage toast)
    {
        // Thread-safe event invocation: capture delegate to local variable
        var handler = OnShow;
        handler?.Invoke(toast);
    }
}

/// <summary>
/// Represents a toast notification message.
/// </summary>
/// <param name="Message">The text message to display.</param>
/// <param name="Type">The type of toast (Success, Error, Warning, Info).</param>
/// <param name="Duration">Optional duration in milliseconds. Defaults to 5000ms if null.</param>
public record ToastMessage(string Message, ToastType Type, int? Duration = null)
{
    /// <summary>
    /// Unique identifier for this toast instance.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets the effective duration in milliseconds.
    /// </summary>
    public int EffectiveDuration => Duration ?? 5000;
}

/// <summary>
/// The type of toast notification, determining its visual style.
/// </summary>
public enum ToastType
{
    Success,
    Error,
    Warning,
    Info
}
