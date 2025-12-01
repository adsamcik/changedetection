namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for fetching website content.
/// </summary>
public interface IContentFetcher
{
    Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct = default);
}

/// <summary>
/// Options for fetching content.
/// </summary>
public class FetchOptions
{
    public bool UseJavaScript { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public string? ProxyUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string? UserAgent { get; set; }
    public string? WaitForSelector { get; set; }
    public int WaitAfterLoadMs { get; set; }
    public bool CaptureScreenshot { get; set; }
    public int ViewportWidth { get; set; } = 1920;
    public int ViewportHeight { get; set; } = 1080;
}

/// <summary>
/// Result of a content fetch operation.
/// </summary>
public class FetchResult
{
    public bool IsSuccess { get; set; }
    public string? Html { get; set; }
    public byte[]? Screenshot { get; set; }
    public int HttpStatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, string> ResponseHeaders { get; set; } = [];
}
