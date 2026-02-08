namespace ChangeDetection.Core.Entities;

/// <summary>
/// Configuration section name constant for LLM providers.
/// </summary>
public static class LlmProviders
{
    public const string SectionName = "LlmProviders";
}

/// <summary>
/// A single LLM provider entry from appsettings.json.
/// Providers are tried in array order (first = highest priority).
/// </summary>
public class LlmProviderOption
{
    /// <summary>
    /// Unique name for this provider (e.g., "Copilot-Haiku", "Local-Ollama").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Provider type.
    /// </summary>
    public LlmProviderType Type { get; set; }

    /// <summary>
    /// Model identifier (e.g., "claude-3.5-haiku", "gpt-4o", "llama3.1").
    /// </summary>
    public required string Model { get; set; }

    /// <summary>
    /// API endpoint URL (optional, uses provider defaults if not specified).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// API key (optional). For Copilot, not needed.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Whether this provider is enabled. Defaults to true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Timeout in seconds for requests. Defaults to 120.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum retries before failing over. Defaults to 2.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Estimated cost per 1K input tokens (for tracking).
    /// </summary>
    public decimal CostPer1KInput { get; set; }

    /// <summary>
    /// Estimated cost per 1K output tokens (for tracking).
    /// </summary>
    public decimal CostPer1KOutput { get; set; }

    /// <summary>
    /// Validates this option and returns error messages if invalid.
    /// </summary>
    public List<string> Validate()
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("Name is required");
        if (string.IsNullOrWhiteSpace(Model))
            errors.Add($"Model is required for provider '{Name}'");
        if (TimeoutSeconds <= 0)
            errors.Add($"TimeoutSeconds must be positive for provider '{Name}'");
        if (MaxRetries < 0)
            errors.Add($"MaxRetries cannot be negative for provider '{Name}'");
        if (Type is LlmProviderType.OpenAI or LlmProviderType.AzureOpenAI or LlmProviderType.Claude or LlmProviderType.Gemini
            && string.IsNullOrWhiteSpace(ApiKey))
            errors.Add($"ApiKey is required for {Type} provider '{Name}'");
        return errors;
    }
}
