namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for processing user input through LLM pipeline.
/// </summary>
public interface IInputProcessor
{
    /// <summary>
    /// Analyzes user input to determine if it's a URL or natural language.
    /// </summary>
    InputAnalysis Analyze(string input);
    
    /// <summary>
    /// Processes natural language input through the LLM pipeline.
    /// </summary>
    Task<LlmProcessResult> ProcessWithLlmAsync(string input, CancellationToken ct = default);
}

/// <summary>
/// Result of input analysis.
/// </summary>
public class InputAnalysis
{
    public InputType Type { get; set; }
    public string? DetectedUrl { get; set; }
    public string? NormalizedUrl { get; set; }
    public bool IsValid { get; set; }
    public string? ValidationMessage { get; set; }
}

public enum InputType
{
    Unknown,
    Url,
    NaturalLanguage
}

/// <summary>
/// Result of LLM processing.
/// </summary>
public class LlmProcessResult
{
    public bool IsSuccess { get; set; }
    public IntentType Intent { get; set; }
    public ParsedWatchRequest? ParsedRequest { get; set; }
    public bool NeedsClarification { get; set; }
    public List<string> ClarificationQuestions { get; set; } = [];
    public List<SuggestionChip> Suggestions { get; set; } = [];
    public string? Summary { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? CreatedWatchId { get; set; }
}

public enum IntentType
{
    Unknown,
    CreateWatch,
    ModifyWatch,
    DeleteWatch,
    QueryStatus,
    ListWatches,
    Clarify,
    Help
}

/// <summary>
/// Parsed request for creating/modifying a watch.
/// </summary>
public class ParsedWatchRequest
{
    public string? Url { get; set; }
    public string? Name { get; set; }
    public string? CssSelector { get; set; }
    public string? XPathSelector { get; set; }
    public TimeSpan? CheckInterval { get; set; }
    public bool? UseJavaScript { get; set; }
    public List<string>? Tags { get; set; }
    public string? NotificationEmail { get; set; }
    public string? DiscordWebhook { get; set; }
    public string? Description { get; set; }
    public Guid? ExistingWatchId { get; set; }
}

/// <summary>
/// A clickable suggestion chip for the UI.
/// </summary>
public class SuggestionChip
{
    public required string Label { get; set; }
    public required string Value { get; set; }
    public SuggestionType Type { get; set; }
}

public enum SuggestionType
{
    AppendText,
    SetValue,
    ExecuteAction
}
