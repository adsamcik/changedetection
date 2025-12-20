namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for LLM log entries displayed in the UI.
/// </summary>
public class LlmLogEntryDto
{
    public required Guid Id { get; set; }
    public required DateTime Timestamp { get; set; }
    public required string Level { get; set; }
    public required string ProviderName { get; set; }
    public string? Model { get; set; }
    public required string Category { get; set; }
    public required string Message { get; set; }
    public string? PromptPreview { get; set; }
    public string? FullPrompt { get; set; }
    public string? ResponsePreview { get; set; }
    public string? FullResponse { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }
    public long? DurationMs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public bool? IsSuccess { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Response containing LLM logs.
/// </summary>
public class LlmLogsResponse
{
    public required List<LlmLogEntryDto> Logs { get; set; }
    public required int TotalCount { get; set; }
}
