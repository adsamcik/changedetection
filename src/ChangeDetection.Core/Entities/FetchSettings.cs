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
    /// Timeout in seconds for the fetch operation.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
    
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
    /// </summary>
    public bool CaptureScreenshot { get; set; }
    
    /// <summary>
    /// Viewport width for screenshot capture.
    /// </summary>
    public int ViewportWidth { get; set; } = 1920;
    
    /// <summary>
    /// Viewport height for screenshot capture.
    /// </summary>
    public int ViewportHeight { get; set; } = 1080;
}
