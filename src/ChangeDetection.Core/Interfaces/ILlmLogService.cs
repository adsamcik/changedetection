namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for capturing and retrieving LLM operation logs for debugging.
/// Maintains an in-memory ring buffer of recent entries.
/// </summary>
public interface ILlmLogService
{
    /// <summary>
    /// Logs an LLM operation (request/response/error).
    /// </summary>
    void Log(LlmLogEntry entry);

    /// <summary>
    /// Gets recent log entries, most recent first.
    /// </summary>
    IReadOnlyList<LlmLogEntry> GetRecentLogs(int count = 100);

    /// <summary>
    /// Gets log entries for a specific provider.
    /// </summary>
    IReadOnlyList<LlmLogEntry> GetLogsForProvider(string providerName, int count = 50);

    /// <summary>
    /// Clears all log entries.
    /// </summary>
    void Clear();

    /// <summary>
    /// Event raised when a new log entry is added.
    /// Used for real-time streaming to UI.
    /// </summary>
    event Action<LlmLogEntry>? OnLogAdded;
}

/// <summary>
/// A single LLM operation log entry.
/// </summary>
public record LlmLogEntry
{
    /// <summary>Unique identifier for this log entry.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>When this entry was created.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Type of log entry.</summary>
    public required LlmLogLevel Level { get; init; }

    /// <summary>Name of the provider involved.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Model name if available.</summary>
    public string? Model { get; init; }

    /// <summary>Category of the log entry.</summary>
    public required LlmLogCategory Category { get; init; }

    /// <summary>Human-readable message.</summary>
    public required string Message { get; init; }

    /// <summary>Prompt sent to LLM (truncated for large prompts).</summary>
    public string? PromptPreview { get; init; }

    /// <summary>Full prompt sent to LLM.</summary>
    public string? FullPrompt { get; init; }

    /// <summary>Response received from LLM (truncated for large responses).</summary>
    public string? ResponsePreview { get; init; }

    /// <summary>Full response received from LLM.</summary>
    public string? FullResponse { get; init; }

    /// <summary>Error message if this is an error entry.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Exception type if this is an error entry.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Stack trace if this is an error entry.</summary>
    public string? StackTrace { get; init; }

    /// <summary>Duration of the operation in milliseconds.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Number of input tokens.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Number of output tokens.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>Whether the operation was successful.</summary>
    public bool? IsSuccess { get; init; }

    /// <summary>Additional context data.</summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Log level for LLM operations.
/// </summary>
public enum LlmLogLevel
{
    /// <summary>Debug information.</summary>
    Debug,
    /// <summary>Normal operation information.</summary>
    Info,
    /// <summary>Warning that might indicate a problem.</summary>
    Warning,
    /// <summary>Error that prevented operation completion.</summary>
    Error
}

/// <summary>
/// Category of LLM log entry.
/// </summary>
public enum LlmLogCategory
{
    /// <summary>Starting an LLM request.</summary>
    Request,
    /// <summary>Received a response.</summary>
    Response,
    /// <summary>Retrying after failure.</summary>
    Retry,
    /// <summary>Circuit breaker state change.</summary>
    CircuitBreaker,
    /// <summary>Provider fallback occurred.</summary>
    Fallback,
    /// <summary>Provider health change.</summary>
    HealthChange,
    /// <summary>Connection or configuration issue.</summary>
    Connection,
    /// <summary>General error.</summary>
    Error
}
