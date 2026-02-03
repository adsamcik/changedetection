using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Settings for fetching website content.
/// </summary>
public class FetchSettings
{
    /// <summary>
    /// Whether to use a headless browser (Playwright) for JavaScript-rendered pages.
    /// </summary>
    public bool UseJavaScript { get; set; }
    
    /// <summary>
    /// Custom HTTP headers to send with requests.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = [];
    
    /// <summary>
    /// Optional proxy URL.
    /// </summary>
    public string? ProxyUrl { get; set; }
    
    /// <summary>
    /// Legacy timeout in seconds for the fetch operation.
    /// Use <see cref="Timeouts"/> for more granular control.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Granular timeout settings for different phases of the fetch operation.
    /// If null, <see cref="TimeoutSeconds"/> is used with automatic conversion.
    /// </summary>
    public TimeoutSettings? Timeouts { get; set; }

    /// <summary>
    /// Gets effective timeout settings, using granular settings if available.
    /// </summary>
    public TimeoutSettings EffectiveTimeouts => Timeouts ?? TimeoutSettings.FromLegacyTimeout(TimeoutSeconds);
    
    /// <summary>
    /// Custom user agent string.
    /// </summary>
    public string? UserAgent { get; set; }
    
    /// <summary>
    /// Optional selector to wait for before capturing content (for JS-rendered pages).
    /// </summary>
    public string? WaitForSelector { get; set; }
    
    /// <summary>
    /// Additional delay in milliseconds after page load.
    /// </summary>
    public int WaitAfterLoadMs { get; set; }
    
    /// <summary>
    /// Whether to capture a screenshot on each check.
    /// Deprecated: Use <see cref="Screenshot"/> settings instead.
    /// </summary>
    [Obsolete("Use Screenshot.Mode instead. This property is maintained for backward compatibility.")]
    public bool CaptureScreenshot
    {
        get => Screenshot.Mode != ScreenshotMode.None;
        set => Screenshot.Mode = value ? ScreenshotMode.Viewport : ScreenshotMode.None;
    }
    
    /// <summary>
    /// Viewport width for screenshot capture.
    /// </summary>
    public int ViewportWidth { get; set; } = 1920;
    
    /// <summary>
    /// Viewport height for screenshot capture.
    /// </summary>
    public int ViewportHeight { get; set; } = 1080;

    /// <summary>
    /// Detailed screenshot capture settings.
    /// </summary>
    public ScreenshotSettings Screenshot { get; set; } = new();
}
