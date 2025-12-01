namespace ChangeDetection.Core.Entities;

/// <summary>
/// Configuration for an LLM provider.
/// </summary>
public class LlmProviderConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Unique name for this provider configuration.
    /// </summary>
    public required string Name { get; set; }
    
    /// <summary>
    /// The type of provider.
    /// </summary>
    public LlmProviderType ProviderType { get; set; }
    
    /// <summary>
    /// Priority order (lower = higher priority, used first).
    /// </summary>
    public int Priority { get; set; }
    
    /// <summary>
    /// Whether this provider is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>
    /// Estimated cost per 1K input tokens (for cost tracking).
    /// </summary>
    public decimal CostPer1KInputTokens { get; set; }
    
    /// <summary>
    /// Estimated cost per 1K output tokens (for cost tracking).
    /// </summary>
    public decimal CostPer1KOutputTokens { get; set; }
    
    /// <summary>
    /// Maximum retries before failing over to next provider.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
    
    /// <summary>
    /// Timeout in seconds for requests.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
    
    /// <summary>
    /// API endpoint URL.
    /// </summary>
    public string? Endpoint { get; set; }
    
    /// <summary>
    /// Model name/ID to use.
    /// </summary>
    public required string Model { get; set; }
    
    /// <summary>
    /// API key (should be stored securely in production).
    /// </summary>
    public string? ApiKey { get; set; }
    
    /// <summary>
    /// Whether this provider is currently healthy (not in circuit breaker open state).
    /// </summary>
    public bool IsHealthy { get; set; } = true;
    
    /// <summary>
    /// Last error encountered.
    /// </summary>
    public string? LastError { get; set; }
    
    /// <summary>
    /// When the last error occurred.
    /// </summary>
    public DateTime? LastErrorAt { get; set; }
    
    /// <summary>
    /// Total tokens used by this provider.
    /// </summary>
    public long TotalTokensUsed { get; set; }
    
    /// <summary>
    /// Total cost incurred by this provider.
    /// </summary>
    public decimal TotalCost { get; set; }
    
    /// <summary>
    /// When this config was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When this config was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum LlmProviderType
{
    Ollama,
    OpenAI,
    AzureOpenAI,
    Gemini,
    Claude,
    Custom
}
