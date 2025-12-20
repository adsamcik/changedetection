using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for LLM-based error resolution when watch checks fail.
/// Diagnoses issues like selector failures due to website structure changes
/// and attempts to generate corrected selectors.
/// </summary>
public interface IErrorResolutionService
{
    /// <summary>
    /// Attempts to diagnose and resolve an extraction error for a watch.
    /// </summary>
    /// <param name="context">Context about the failed extraction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolution result with diagnosis and potential fix.</returns>
    Task<ErrorResolutionResult> TryResolveAsync(ErrorResolutionContext context, CancellationToken ct = default);
    
    /// <summary>
    /// Validates a proposed selector fix against the current page content.
    /// </summary>
    /// <param name="html">Current page HTML.</param>
    /// <param name="proposedSelector">The proposed selector fix.</param>
    /// <param name="selectorType">Type of selector (CSS, XPath).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    Task<SelectorValidationResult> ValidateSelectorFixAsync(
        string html,
        string proposedSelector,
        SelectorType selectorType,
        CancellationToken ct = default);
}

/// <summary>
/// Context for error resolution attempt.
/// </summary>
public class ErrorResolutionContext
{
    /// <summary>
    /// The watch that failed.
    /// </summary>
    public required WatchedSite Watch { get; init; }
    
    /// <summary>
    /// Current page HTML content.
    /// </summary>
    public required string CurrentHtml { get; init; }
    
    /// <summary>
    /// The error message from the failed check.
    /// </summary>
    public required string ErrorMessage { get; init; }
    
    /// <summary>
    /// Type of error encountered.
    /// </summary>
    public ErrorType ErrorType { get; init; }
    
    /// <summary>
    /// Previous successful content snapshot for comparison.
    /// </summary>
    public string? PreviousContent { get; init; }
    
    /// <summary>
    /// Previous successful HTML for structure comparison.
    /// </summary>
    public string? PreviousHtml { get; init; }
    
    /// <summary>
    /// Number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures { get; init; }
}

/// <summary>
/// Type of error that occurred during check.
/// </summary>
public enum ErrorType
{
    /// <summary>
    /// Selector returned empty/no match.
    /// </summary>
    SelectorNoMatch,
    
    /// <summary>
    /// Selector syntax is now invalid.
    /// </summary>
    SelectorInvalid,
    
    /// <summary>
    /// Content extraction succeeded but returned unexpected format.
    /// </summary>
    ContentFormatChanged,
    
    /// <summary>
    /// Schema extraction failed due to structure drift.
    /// </summary>
    SchemaDrift,
    
    /// <summary>
    /// HTTP fetch failed.
    /// </summary>
    FetchFailed,
    
    /// <summary>
    /// Unknown error type.
    /// </summary>
    Unknown
}

/// <summary>
/// Result of an error resolution attempt.
/// </summary>
public record ErrorResolutionResult
{
    /// <summary>
    /// Whether the resolution was successful.
    /// </summary>
    public bool IsResolved { get; init; }
    
    /// <summary>
    /// Whether a fix was applied automatically.
    /// </summary>
    public bool AutoFixApplied { get; init; }
    
    /// <summary>
    /// Diagnosis of the problem.
    /// </summary>
    public required string Diagnosis { get; init; }
    
    /// <summary>
    /// Suggested action for the user if auto-fix not possible.
    /// </summary>
    public string? SuggestedAction { get; init; }
    
    /// <summary>
    /// New CSS selector if the fix involves a selector change.
    /// </summary>
    public string? NewCssSelector { get; init; }
    
    /// <summary>
    /// New XPath selector if the fix involves a selector change.
    /// </summary>
    public string? NewXPathSelector { get; init; }
    
    /// <summary>
    /// Confidence in the fix (0-1).
    /// </summary>
    public float Confidence { get; init; }
    
    /// <summary>
    /// LLM reasoning for the diagnosis and fix.
    /// </summary>
    public string? Reasoning { get; init; }
    
    /// <summary>
    /// Whether user approval is recommended before applying.
    /// </summary>
    public bool RequiresUserApproval { get; init; }
    
    /// <summary>
    /// Extracted sample from the new selector to show user.
    /// </summary>
    public string? ExtractedSample { get; init; }
    
    /// <summary>
    /// Number of matches the new selector found.
    /// </summary>
    public int MatchCount { get; init; }
    
    /// <summary>
    /// Whether the website structure has fundamentally changed.
    /// </summary>
    public bool MajorStructureChange { get; init; }
}

/// <summary>
/// Result of validating a selector fix.
/// </summary>
public class SelectorValidationResult
{
    /// <summary>
    /// Whether the selector is valid and matches content.
    /// </summary>
    public bool IsValid { get; init; }
    
    /// <summary>
    /// Number of elements matched.
    /// </summary>
    public int MatchCount { get; init; }
    
    /// <summary>
    /// Sample of extracted content.
    /// </summary>
    public string? ExtractedSample { get; init; }
    
    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
