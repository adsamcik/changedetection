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
    /// Email settings for notifications.
    /// </summary>
    public EmailSettings? Email { get; set; }
    
    /// <summary>
    /// How long to keep snapshots (in days).
    /// </summary>
    public int SnapshotRetentionDays { get; set; } = 30;
    
    /// <summary>
    /// How long to keep change events (in days).
    /// </summary>
    public int ChangeEventRetentionDays { get; set; } = 90;
    
    /// <summary>
    /// Whether to use LLM for change summarization by default.
    /// </summary>
    public bool UseLlmForSummaries { get; set; } = true;
    
    /// <summary>
    /// Maximum Playwright browser instances.
    /// </summary>
    public int MaxPlaywrightInstances { get; set; } = 3;
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
