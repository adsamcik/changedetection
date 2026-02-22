using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Chain of LLM providers with fallback support.
/// </summary>
public interface ILlmProviderChain
{
    /// <summary>
    /// Executes a completion request, falling back to other providers if needed.
    /// </summary>
    Task<LlmResponse> ExecuteAsync(string prompt, LlmRequestOptions? options = null, CancellationToken ct = default);
    
    /// <summary>
    /// Executes a streaming completion request, yielding tokens as they arrive.
    /// </summary>
    IAsyncEnumerable<LlmStreamChunk> ExecuteStreamingAsync(string prompt, LlmRequestOptions? options = null, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the currently available providers ordered by priority.
    /// </summary>
    Task<IEnumerable<LlmProviderConfig>> GetAvailableProvidersAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets the health status of all providers.
    /// </summary>
    Task<IEnumerable<ProviderHealthStatus>> GetHealthStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks whether any enabled, healthy provider uses a non-small (large) model.
    /// </summary>
    Task<bool> HasLargeModelAsync(CancellationToken ct = default);
}

/// <summary>
/// A chunk of streaming LLM response.
/// </summary>
public class LlmStreamChunk
{
    /// <summary>Type of chunk.</summary>
    public LlmStreamChunkType Type { get; set; }
    
    /// <summary>Text content for Content chunks.</summary>
    public string? Text { get; set; }
    
    /// <summary>Provider being used.</summary>
    public string? ProviderName { get; set; }
    
    /// <summary>Model being used.</summary>
    public string? Model { get; set; }
    
    /// <summary>Error message for Error chunks.</summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>Final response for Complete chunks.</summary>
    public LlmResponse? FinalResponse { get; set; }
}

/// <summary>
/// Type of streaming chunk.
/// </summary>
public enum LlmStreamChunkType
{
    /// <summary>Stream is starting.</summary>
    Start,
    /// <summary>Content token.</summary>
    Content,
    /// <summary>Stream completed successfully.</summary>
    Complete,
    /// <summary>Error occurred.</summary>
    Error
}

/// <summary>
/// Options for LLM requests.
/// </summary>
public class LlmRequestOptions
{
    /// <summary>
    /// Specific provider to use (bypasses chain).
    /// </summary>
    public string? ProviderName { get; set; }
    
    /// <summary>
    /// Temperature for generation (0-1).
    /// </summary>
    public float Temperature { get; set; } = 0.7f;
    
    /// <summary>
    /// Maximum tokens to generate.
    /// </summary>
    public int MaxTokens { get; set; } = 1024;
    
    /// <summary>
    /// Usage type for tracking.
    /// </summary>
    public LlmUsageType UsageType { get; set; }
    
    /// <summary>
    /// Related watch ID for tracking.
    /// </summary>
    public Guid? WatchedSiteId { get; set; }
    
    /// <summary>
    /// Whether to parse response as JSON.
    /// </summary>
    public bool ExpectJson { get; set; }

    /// <summary>
    /// Enable compact mode for smaller models. When true, reduces MaxTokens by 40%
    /// and sets temperature to 0.1 for more deterministic outputs.
    /// Auto-detected based on model name if null.
    /// </summary>
    public bool? CompactMode { get; set; }

    /// <summary>
    /// When true, prefers a large (non-small) model for this request.
    /// Falls back to any available model if no large model is available.
    /// </summary>
    public bool PreferLargeModel { get; set; }
}

/// <summary>
/// Response from an LLM provider.
/// </summary>
public class LlmResponse
{
    public bool IsSuccess { get; set; }
    public string? Content { get; set; }
    public string? ProviderUsed { get; set; }
    public string? Model { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public int FailedProviderCount { get; set; }
}

/// <summary>
/// Health status of a provider.
/// </summary>
public class ProviderHealthStatus
{
    public required string ProviderName { get; set; }
    public bool IsHealthy { get; set; }
    public bool IsEnabled { get; set; }
    public int Priority { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public int FailureCount { get; set; }
    public DateTime? CircuitBreakerResetAt { get; set; }
}
