namespace ChangeDetection.Core.Entities;

/// <summary>
/// Mode for capturing screenshots during content fetching.
/// </summary>
public enum ScreenshotMode
{
    /// <summary>
    /// No screenshots will be captured.
    /// </summary>
    None,

    /// <summary>
    /// Capture only the visible viewport.
    /// </summary>
    Viewport,

    /// <summary>
    /// Capture the full scrollable page.
    /// </summary>
    FullPage,

    /// <summary>
    /// Capture only the monitored element(s) based on CSS/XPath selector.
    /// </summary>
    ElementOnly,

    /// <summary>
    /// Capture both the full page and the monitored element(s).
    /// </summary>
    FullPageAndElement
}

/// <summary>
/// Settings for screenshot capture during content fetching.
/// </summary>
public class ScreenshotSettings
{
    /// <summary>
    /// Screenshot capture mode.
    /// </summary>
    public ScreenshotMode Mode { get; set; } = ScreenshotMode.None;

    /// <summary>
    /// Whether to capture a screenshot on every check (vs only when changes detected).
    /// </summary>
    public bool CaptureOnEveryCheck { get; set; } = true;

    /// <summary>
    /// Whether to capture a screenshot when a change is detected.
    /// </summary>
    public bool CaptureOnChange { get; set; } = true;

    /// <summary>
    /// Image quality for JPEG format (1-100). Only used when Format is Jpeg.
    /// </summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>
    /// Image format for screenshots.
    /// </summary>
    public ScreenshotFormat Format { get; set; } = ScreenshotFormat.Png;

    /// <summary>
    /// Scale factor for screenshots (0.1 to 2.0). 1.0 = 100%.
    /// Lower values reduce file size but decrease quality.
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// Padding in pixels around element screenshots.
    /// </summary>
    public int ElementPadding { get; set; } = 10;

    /// <summary>
    /// Whether to highlight the monitored element in screenshots.
    /// </summary>
    public bool HighlightElement { get; set; } = true;

    /// <summary>
    /// Color of the element highlight border (hex format).
    /// </summary>
    public string HighlightColor { get; set; } = "#FF6B6B";

    /// <summary>
    /// Width of the element highlight border in pixels.
    /// </summary>
    public int HighlightBorderWidth { get; set; } = 3;

    /// <summary>
    /// Maximum width for screenshots. Images will be scaled down if wider.
    /// Null means no limit.
    /// </summary>
    public int? MaxWidth { get; set; }

    /// <summary>
    /// Maximum height for screenshots. Images will be scaled down if taller.
    /// Null means no limit.
    /// </summary>
    public int? MaxHeight { get; set; }

    /// <summary>
    /// Whether screenshots are enabled (convenience property).
    /// </summary>
    public bool IsEnabled => Mode != ScreenshotMode.None;

    /// <summary>
    /// Whether element-specific screenshots should be captured.
    /// </summary>
    public bool CapturesElement => Mode is ScreenshotMode.ElementOnly or ScreenshotMode.FullPageAndElement;

    /// <summary>
    /// Whether full page or viewport screenshots should be captured.
    /// </summary>
    public bool CapturesFullPage => Mode is ScreenshotMode.FullPage or ScreenshotMode.FullPageAndElement or ScreenshotMode.Viewport;
}

/// <summary>
/// Image format for screenshots.
/// </summary>
public enum ScreenshotFormat
{
    /// <summary>
    /// PNG format (lossless, larger file size).
    /// </summary>
    Png,

    /// <summary>
    /// JPEG format (lossy, smaller file size).
    /// </summary>
    Jpeg
}
