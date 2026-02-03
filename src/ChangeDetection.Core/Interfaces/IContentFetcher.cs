namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for fetching website content.
/// </summary>
public interface IContentFetcher
{
    Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct = default);
}

/// <summary>
/// Timeout configuration for content fetching operations.
/// </summary>
public class TimeoutSettings
{
    /// <summary>
    /// Maximum time to wait for initial server response (TCP connect + first byte).
    /// Default: 15 seconds.
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Maximum time to wait for the page to load (DOM content loaded).
    /// Default: 30 seconds.
    /// </summary>
    public int NavigationTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum time to wait for network to become idle (no requests for 500ms).
    /// Default: 30 seconds.
    /// </summary>
    public int NetworkIdleTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum time to wait for a specific selector to appear.
    /// Default: 15 seconds.
    /// </summary>
    public int SelectorTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Overall maximum time for the entire fetch operation.
    /// Default: 60 seconds.
    /// </summary>
    public int TotalTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Creates a TimeoutSettings from a single legacy timeout value.
    /// </summary>
    public static TimeoutSettings FromLegacyTimeout(int timeoutSeconds)
    {
        return new TimeoutSettings
        {
            ResponseTimeoutSeconds = Math.Min(timeoutSeconds, 15),
            NavigationTimeoutSeconds = timeoutSeconds,
            NetworkIdleTimeoutSeconds = timeoutSeconds,
            SelectorTimeoutSeconds = Math.Min(timeoutSeconds, 15),
            TotalTimeoutSeconds = (int)(timeoutSeconds * 1.5)
        };
    }
}

/// <summary>
/// Options for fetching content.
/// </summary>
public class FetchOptions
{
    public bool UseJavaScript { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public string? ProxyUrl { get; set; }

    /// <summary>
    /// Legacy single timeout. Use <see cref="Timeouts"/> for more granular control.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Granular timeout settings. If null, <see cref="TimeoutSeconds"/> is used via legacy conversion.
    /// </summary>
    public TimeoutSettings? Timeouts { get; set; }

    /// <summary>
    /// Gets effective timeout settings, using granular settings if available or converting from legacy.
    /// </summary>
    public TimeoutSettings EffectiveTimeouts => Timeouts ?? TimeoutSettings.FromLegacyTimeout(TimeoutSeconds);

    public string? UserAgent { get; set; }
    public string? WaitForSelector { get; set; }
    public int WaitAfterLoadMs { get; set; }
    
    /// <summary>
    /// Legacy screenshot flag. Use <see cref="ScreenshotSettings"/> for more control.
    /// </summary>
    public bool CaptureScreenshot { get; set; }
    
    public int ViewportWidth { get; set; } = 1920;
    public int ViewportHeight { get; set; } = 1080;

    /// <summary>
    /// Detailed screenshot capture settings.
    /// </summary>
    public ScreenshotCaptureOptions ScreenshotSettings { get; set; } = new();

    /// <summary>
    /// CSS or XPath selector for element-specific screenshots.
    /// If set, element screenshots will be captured based on this selector.
    /// </summary>
    public string? ElementSelector { get; set; }
}

/// <summary>
/// Options for screenshot capture during content fetching.
/// </summary>
public class ScreenshotCaptureOptions
{
    /// <summary>
    /// Whether to capture a full page screenshot.
    /// </summary>
    public bool CaptureFullPage { get; set; }

    /// <summary>
    /// Whether to capture a viewport screenshot (visible area only).
    /// </summary>
    public bool CaptureViewport { get; set; }

    /// <summary>
    /// Whether to capture an element-specific screenshot.
    /// </summary>
    public bool CaptureElement { get; set; }

    /// <summary>
    /// Image format for screenshots.
    /// </summary>
    public string Format { get; set; } = "png";

    /// <summary>
    /// JPEG quality (1-100) when Format is "jpeg".
    /// </summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>
    /// Padding in pixels around element screenshots.
    /// </summary>
    public int ElementPadding { get; set; } = 10;

    /// <summary>
    /// Whether to highlight the element in screenshots.
    /// </summary>
    public bool HighlightElement { get; set; }

    /// <summary>
    /// Color of the highlight border (CSS color format).
    /// </summary>
    public string HighlightColor { get; set; } = "#FF6B6B";

    /// <summary>
    /// Width of the highlight border in pixels.
    /// </summary>
    public int HighlightBorderWidth { get; set; } = 3;

    /// <summary>
    /// Whether any screenshot capture is enabled.
    /// </summary>
    public bool IsEnabled => CaptureFullPage || CaptureViewport || CaptureElement;
}

/// <summary>
/// Category of fetch error for intelligent error handling.
/// </summary>
public enum FetchErrorCategory
{
    /// <summary>No error occurred.</summary>
    None,

    /// <summary>DNS resolution failed - hostname not found.</summary>
    DnsResolutionFailed,

    /// <summary>Could not establish TCP connection - server unreachable or refused connection.</summary>
    ConnectionFailed,

    /// <summary>SSL/TLS handshake failed - certificate issue or protocol mismatch.</summary>
    SslError,

    /// <summary>Server did not respond within the response timeout - server may be overloaded.</summary>
    ResponseTimeout,

    /// <summary>Page did not finish loading within navigation timeout - page may be large or server slow.</summary>
    NavigationTimeout,

    /// <summary>Network did not become idle - page has continuous background requests.</summary>
    NetworkIdleTimeout,

    /// <summary>Specific selector not found within timeout - element may not exist or have different selector.</summary>
    SelectorTimeout,

    /// <summary>Overall operation exceeded total timeout limit.</summary>
    TotalTimeout,

    /// <summary>HTTP error response (4xx or 5xx status code).</summary>
    HttpError,

    /// <summary>Response exceeded maximum allowed size.</summary>
    ResponseTooLarge,

    /// <summary>Operation was cancelled by the caller.</summary>
    Cancelled,

    /// <summary>Unexpected error not covered by other categories.</summary>
    Unknown
}

/// <summary>
/// Detailed fetch progress information for error diagnosis.
/// </summary>
public class FetchProgress
{
    /// <summary>Time taken for DNS resolution (if tracked).</summary>
    public long? DnsResolutionMs { get; set; }

    /// <summary>Time taken to establish connection.</summary>
    public long? ConnectionMs { get; set; }

    /// <summary>Time to receive first byte from server.</summary>
    public long? TimeToFirstByteMs { get; set; }

    /// <summary>Time for page navigation to complete.</summary>
    public long? NavigationMs { get; set; }

    /// <summary>Time waiting for network idle.</summary>
    public long? NetworkIdleMs { get; set; }

    /// <summary>Time waiting for selector.</summary>
    public long? SelectorWaitMs { get; set; }

    /// <summary>Number of network requests made during page load.</summary>
    public int? NetworkRequestCount { get; set; }

    /// <summary>Number of network requests still pending when timeout occurred.</summary>
    public int? PendingRequestCount { get; set; }

    /// <summary>URLs of pending requests when timeout occurred (limited to first 10).</summary>
    public List<string> PendingRequestUrls { get; set; } = [];

    /// <summary>Whether initial response was received before timeout.</summary>
    public bool ReceivedInitialResponse { get; set; }

    /// <summary>Whether page started loading content.</summary>
    public bool PageLoadStarted { get; set; }

    /// <summary>Last known page state/URL during fetch.</summary>
    public string? LastKnownUrl { get; set; }
}

/// <summary>
/// Result of a content fetch operation.
/// </summary>
public class FetchResult
{
    public bool IsSuccess { get; set; }
    public string? Html { get; set; }
    
    /// <summary>
    /// Full page or viewport screenshot data.
    /// </summary>
    public byte[]? Screenshot { get; set; }

    /// <summary>
    /// Element-specific screenshot data (cropped to the monitored element).
    /// </summary>
    public byte[]? ElementScreenshot { get; set; }

    /// <summary>
    /// Bounding box of the element in the page (for highlighting in full screenshot).
    /// </summary>
    public ElementBoundingBox? ElementBoundingBox { get; set; }
    
    public int HttpStatusCode { get; set; }
    public long DurationMs { get; set; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Category of error for programmatic handling.
    /// </summary>
    public FetchErrorCategory ErrorCategory { get; set; } = FetchErrorCategory.None;

    /// <summary>
    /// Detailed error message with diagnostic information.
    /// </summary>
    public string? DetailedError { get; set; }

    /// <summary>
    /// Progress information showing what succeeded before failure.
    /// </summary>
    public FetchProgress? Progress { get; set; }

    /// <summary>
    /// Suggestions for resolving the error.
    /// </summary>
    public List<string> Suggestions { get; set; } = [];

    public Dictionary<string, string> ResponseHeaders { get; set; } = [];

    /// <summary>
    /// Creates a descriptive summary of the fetch result for logging/display.
    /// </summary>
    public string GetDiagnosticSummary()
    {
        if (IsSuccess)
            return $"Success: {HttpStatusCode} in {DurationMs}ms";

        var parts = new List<string>
        {
            $"Failed: {ErrorCategory}"
        };

        if (HttpStatusCode > 0)
            parts.Add($"HTTP {HttpStatusCode}");

        parts.Add($"after {DurationMs}ms");

        if (Progress != null)
        {
            if (Progress.ReceivedInitialResponse)
                parts.Add("(response received)");
            else
                parts.Add("(no response)");

            if (Progress.PendingRequestCount > 0)
                parts.Add($"{Progress.PendingRequestCount} pending requests");
        }

        return string.Join(" ", parts);
    }
}

/// <summary>
/// Represents the bounding box of an element on the page.
/// </summary>
public class ElementBoundingBox
{
    /// <summary>
    /// X coordinate of the element's top-left corner.
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// Y coordinate of the element's top-left corner.
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// Width of the element in pixels.
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// Height of the element in pixels.
    /// </summary>
    public double Height { get; set; }
}
