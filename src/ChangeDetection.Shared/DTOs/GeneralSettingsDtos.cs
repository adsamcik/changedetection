namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for reading general application settings.
/// </summary>
public class GeneralSettingsDto
{
    /// <summary>
    /// Default check interval in minutes for new watches.
    /// </summary>
    public int DefaultCheckIntervalMinutes { get; set; } = 60;
    
    /// <summary>
    /// Maximum concurrent watch checks.
    /// </summary>
    public int MaxConcurrentChecks { get; set; } = 5;
    
    /// <summary>
    /// How long to keep snapshots (in days).
    /// </summary>
    public int SnapshotRetentionDays { get; set; } = 30;
    
    /// <summary>
    /// How long to keep change events (in days).
    /// </summary>
    public int ChangeEventRetentionDays { get; set; } = 90;
    
    /// <summary>
    /// Maximum Playwright browser instances.
    /// </summary>
    public int MaxPlaywrightInstances { get; set; } = 3;
    
    /// <summary>
    /// Whether to use LLM for change summarization by default.
    /// </summary>
    public bool UseLlmForSummaries { get; set; } = true;
    
    /// <summary>
    /// Default user agent for HTTP requests.
    /// </summary>
    public string? DefaultUserAgent { get; set; }
    
    /// <summary>
    /// Default timeout in seconds for fetch operations.
    /// </summary>
    public int DefaultFetchTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Whether to enable debug logging for LLM operations.
    /// </summary>
    public bool EnableLlmDebugLogging { get; set; } = false;
    
    /// <summary>
    /// Maximum number of retries for failed checks.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;
    
    /// <summary>
    /// Delay between retries in seconds.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 60;
}

/// <summary>
/// DTO for updating general application settings.
/// </summary>
public class GeneralSettingsUpdateDto
{
    /// <summary>
    /// Default check interval in minutes for new watches.
    /// </summary>
    public int? DefaultCheckIntervalMinutes { get; set; }
    
    /// <summary>
    /// Maximum concurrent watch checks.
    /// </summary>
    public int? MaxConcurrentChecks { get; set; }
    
    /// <summary>
    /// How long to keep snapshots (in days).
    /// </summary>
    public int? SnapshotRetentionDays { get; set; }
    
    /// <summary>
    /// How long to keep change events (in days).
    /// </summary>
    public int? ChangeEventRetentionDays { get; set; }
    
    /// <summary>
    /// Maximum Playwright browser instances.
    /// </summary>
    public int? MaxPlaywrightInstances { get; set; }
    
    /// <summary>
    /// Whether to use LLM for change summarization by default.
    /// </summary>
    public bool? UseLlmForSummaries { get; set; }
    
    /// <summary>
    /// Default user agent for HTTP requests.
    /// </summary>
    public string? DefaultUserAgent { get; set; }
    
    /// <summary>
    /// Default timeout in seconds for fetch operations.
    /// </summary>
    public int? DefaultFetchTimeoutSeconds { get; set; }
    
    /// <summary>
    /// Whether to enable debug logging for LLM operations.
    /// </summary>
    public bool? EnableLlmDebugLogging { get; set; }
    
    /// <summary>
    /// Maximum number of retries for failed checks.
    /// </summary>
    public int? MaxRetryAttempts { get; set; }
    
    /// <summary>
    /// Delay between retries in seconds.
    /// </summary>
    public int? RetryDelaySeconds { get; set; }
}
