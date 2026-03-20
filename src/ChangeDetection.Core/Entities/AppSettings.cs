namespace ChangeDetection.Core.Entities;

/// <summary>
/// Global application settings.
/// </summary>
public class AppSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Default check interval for new watches.
    /// </summary>
    public TimeSpan DefaultCheckInterval { get; set; } = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// Maximum concurrent watch checks.
    /// </summary>
    public int MaxConcurrentChecks { get; set; } = 5;
    
    /// <summary>
    /// Maximum concurrent pipeline executions.
    /// Controls how many watch setup pipelines can run in parallel.
    /// </summary>
    public int MaxConcurrentPipelines { get; set; } = 1;
    
    /// <summary>
    /// Email settings for notifications.
    /// </summary>
    public EmailSettings? Email { get; set; }

    /// <summary>
    /// Global notification channel defaults used when a watch does not define its own destinations.
    /// </summary>
    public NotificationSettings DefaultNotifications { get; set; } = new();
    
    /// <summary>
    /// How long to keep snapshots (in days).
    /// </summary>
    public int SnapshotRetentionDays { get; set; } = 30;
    
    /// <summary>
    /// How long to keep change events (in days).
    /// </summary>
    public int ChangeEventRetentionDays { get; set; } = 90;
    
    /// <summary>
    /// Hard ceiling for snapshot retention (in days). Per-watch values cannot exceed this.
    /// Set to 0 to disable the ceiling.
    /// </summary>
    public int MaxRetentionDays { get; set; } = 180;
    
    /// <summary>
    /// Whether to use LLM for change summarization by default.
    /// </summary>
    public bool UseLlmForSummaries { get; set; } = true;
    
    /// <summary>
    /// Maximum Playwright browser instances.
    /// </summary>
    public int MaxPlaywrightInstances { get; set; } = 3;
    
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
    public bool EnableLlmDebugLogging { get; set; }
    
    /// <summary>
    /// Maximum number of retries for failed checks.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;
    
    /// <summary>
    /// Delay between retries in seconds.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 60;
    
    /// <summary>
    /// Maximum pending queue items per user.
    /// Set to 0 for unlimited.
    /// </summary>
    public int MaxPendingItemsPerUser { get; set; } = 10;
    
    /// <summary>
    /// Maximum concurrent processing items per user.
    /// Set to 0 for unlimited.
    /// </summary>
    public int MaxConcurrentItemsPerUser { get; set; } = 2;
}

/// <summary>
/// SMTP email settings.
/// </summary>
public class EmailSettings
{
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; } = "Change Detection";
}
