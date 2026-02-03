using System.Collections.Concurrent;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.LLM;

/// <summary>
/// In-memory LLM log service with ring buffer for debugging.
/// </summary>
public class LlmLogService : ILlmLogService
{
    private readonly ConcurrentQueue<LlmLogEntry> _entries = new();
    private readonly int _maxEntries;
    private readonly object _pruneLock = new();

    /// <inheritdoc />
    public event Action<LlmLogEntry>? OnLogAdded;

    public LlmLogService(int maxEntries = 500)
    {
        _maxEntries = maxEntries;
    }

    /// <inheritdoc />
    public void Log(LlmLogEntry entry)
    {
        _entries.Enqueue(entry);
        PruneIfNeeded();
        OnLogAdded?.Invoke(entry);
    }

    /// <inheritdoc />
    public IReadOnlyList<LlmLogEntry> GetRecentLogs(int count = 100)
    {
        return _entries
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<LlmLogEntry> GetLogsForProvider(string providerName, int count = 50)
    {
        return _entries
            .Where(e => e.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <inheritdoc />
    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    private void PruneIfNeeded()
    {
        // Only prune when significantly over limit to avoid frequent locking
        if (_entries.Count <= _maxEntries * 1.2)
            return;

        lock (_pruneLock)
        {
            // Double-check after acquiring lock
            while (_entries.Count > _maxEntries)
            {
                _entries.TryDequeue(out _);
            }
        }
    }
}

/// <summary>
/// Extension methods for easy LLM logging.
/// </summary>
public static class LlmLogServiceExtensions
{
    private const int MaxPreviewLength = 500;

    /// <summary>
    /// Logs an LLM request starting and returns a RequestId for correlation.
    /// </summary>
    public static Guid LogRequest(
        this ILlmLogService service,
        string providerName,
        string? model,
        string prompt,
        Dictionary<string, string>? metadata = null)
    {
        var requestId = Guid.NewGuid();
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Debug,
            ProviderName = providerName,
            Model = model,
            Category = LlmLogCategory.Request,
            RequestId = requestId,
            Message = $"Sending request to {providerName}",
            PromptPreview = TruncatePreview(prompt),
            FullPrompt = prompt,
            Metadata = metadata
        });
        return requestId;
    }

    /// <summary>
    /// Logs a successful LLM response.
    /// </summary>
    public static void LogResponse(
        this ILlmLogService service,
        string providerName,
        string? model,
        string response,
        long durationMs,
        int inputTokens,
        int outputTokens,
        Guid? requestId = null)
    {
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = providerName,
            Model = model,
            Category = LlmLogCategory.Response,
            RequestId = requestId,
            Message = $"Received response from {providerName} in {durationMs}ms",
            ResponsePreview = TruncatePreview(response),
            FullResponse = response,
            DurationMs = durationMs,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            IsSuccess = true
        });
    }

    /// <summary>
    /// Logs an LLM error.
    /// </summary>
    public static void LogError(
        this ILlmLogService service,
        string providerName,
        string? model,
        Exception exception,
        string? prompt = null,
        Guid? requestId = null)
    {
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Error,
            ProviderName = providerName,
            Model = model,
            Category = LlmLogCategory.Error,
            RequestId = requestId,
            Message = $"Error from {providerName}: {exception.Message}",
            PromptPreview = prompt != null ? TruncatePreview(prompt) : null,
            FullPrompt = prompt,
            ErrorMessage = exception.Message,
            ExceptionType = exception.GetType().Name,
            StackTrace = exception.StackTrace,
            IsSuccess = false
        });
    }

    /// <summary>
    /// Logs a retry attempt.
    /// </summary>
    public static void LogRetry(
        this ILlmLogService service,
        string providerName,
        string? model,
        int attemptNumber,
        string? reason = null)
    {
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Warning,
            ProviderName = providerName,
            Model = model,
            Category = LlmLogCategory.Retry,
            Message = $"Retrying {providerName}, attempt {attemptNumber}" + (reason != null ? $": {reason}" : ""),
            Metadata = new Dictionary<string, string> { ["attemptNumber"] = attemptNumber.ToString() }
        });
    }

    /// <summary>
    /// Logs circuit breaker state change.
    /// </summary>
    public static void LogCircuitBreaker(
        this ILlmLogService service,
        string providerName,
        bool isOpen,
        string? reason = null)
    {
        var state = isOpen ? "OPENED" : "CLOSED";
        service.Log(new LlmLogEntry
        {
            Level = isOpen ? LlmLogLevel.Warning : LlmLogLevel.Info,
            ProviderName = providerName,
            Category = LlmLogCategory.CircuitBreaker,
            Message = $"Circuit breaker {state} for {providerName}" + (reason != null ? $": {reason}" : ""),
            Metadata = new Dictionary<string, string> { ["circuitState"] = state }
        });
    }

    /// <summary>
    /// Logs provider fallback.
    /// </summary>
    public static void LogFallback(
        this ILlmLogService service,
        string fromProvider,
        string toProvider,
        string reason)
    {
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Warning,
            ProviderName = fromProvider,
            Category = LlmLogCategory.Fallback,
            Message = $"Falling back from {fromProvider} to {toProvider}: {reason}",
            Metadata = new Dictionary<string, string>
            {
                ["fromProvider"] = fromProvider,
                ["toProvider"] = toProvider
            }
        });
    }

    /// <summary>
    /// Logs circuit breaker preventing request.
    /// </summary>
    public static void LogCircuitBreakerBlocked(
        this ILlmLogService service,
        string providerName)
    {
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Warning,
            ProviderName = providerName,
            Category = LlmLogCategory.CircuitBreaker,
            Message = $"Request blocked by circuit breaker for {providerName} - provider temporarily unavailable",
            IsSuccess = false
        });
    }

    /// <summary>
    /// Logs a connection or configuration error.
    /// </summary>
    public static void LogConnectionError(
        this ILlmLogService service,
        string providerName,
        string? model,
        string message,
        Exception? exception = null)
    {
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Error,
            ProviderName = providerName,
            Model = model,
            Category = LlmLogCategory.Connection,
            Message = message,
            ErrorMessage = exception?.Message,
            ExceptionType = exception?.GetType().Name,
            StackTrace = exception?.StackTrace,
            IsSuccess = false
        });
    }

    private static string TruncatePreview(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (text.Length <= MaxPreviewLength)
            return text;

        return text[..MaxPreviewLength] + $"... ({text.Length - MaxPreviewLength} more chars)";
    }
}
