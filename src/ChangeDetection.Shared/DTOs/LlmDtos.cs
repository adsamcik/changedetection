namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// Request to process input through LLM.
/// </summary>
public class ProcessInputRequest
{
    public required string Input { get; set; }
    public string? ConversationContext { get; set; }
}

/// <summary>
/// Response from LLM input processing.
/// </summary>
public class ProcessInputResponse
{
    public bool IsSuccess { get; set; }
    public string Intent { get; set; } = "Unknown";
    public ParsedWatchRequestDto? ParsedRequest { get; set; }
    public bool NeedsClarification { get; set; }
    public List<string> ClarificationQuestions { get; set; } = new();
    public List<SuggestionChipDto> Suggestions { get; set; } = new();
    public string? Summary { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CreatedWatchId { get; set; }
    /// <summary>
    /// Indicates whether the request should be handed off to the multi-agent setup flow.
    /// </summary>
    public bool RequiresSetupFlow { get; set; }
}

/// <summary>
/// Parsed watch request from LLM.
/// </summary>
public class ParsedWatchRequestDto
{
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? CssSelector { get; set; }
    public string? XPathSelector { get; set; }
    public int? CheckIntervalMinutes { get; set; }
    public bool? UseJavaScript { get; set; }
    public List<string>? Tags { get; set; }
    public string? NotificationEmail { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Suggestion chip for the UI.
/// </summary>
public class SuggestionChipDto
{
    public required string Label { get; set; }
    public required string Value { get; set; }
    public string Type { get; set; } = "AppendText";
}
