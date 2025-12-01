namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for extracting and processing content from HTML.
/// </summary>
public interface IContentExtractor
{
    /// <summary>
    /// Extracts text content from HTML, optionally using selectors.
    /// </summary>
    string ExtractText(string html, string? cssSelector = null, string? xpathSelector = null);
    
    /// <summary>
    /// Extracts the selected HTML fragment using selectors.
    /// </summary>
    string? ExtractHtml(string html, string? cssSelector = null, string? xpathSelector = null);
    
    /// <summary>
    /// Computes a hash of the content for change detection.
    /// </summary>
    string ComputeHash(string content);
    
    /// <summary>
    /// Extracts the page title from HTML.
    /// </summary>
    string? ExtractTitle(string html);
    
    /// <summary>
    /// Cleans HTML by removing scripts, styles, and normalizing whitespace.
    /// </summary>
    string CleanHtml(string html);
}
