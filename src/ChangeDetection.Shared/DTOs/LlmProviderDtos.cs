namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for LLM provider configuration (display).
/// </summary>
public class LlmProviderDto
{
    public string Id { get; set; } = "";
    public string ProviderType { get; set; } = "OpenAI";
    public string? ModelId { get; set; }
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }
    public int Priority { get; set; }
    public int MaxTokens { get; set; }
    public decimal CostPerInputToken { get; set; }
    public decimal CostPerOutputToken { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsHealthy { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastUsed { get; set; }
}

/// <summary>
/// DTO for creating/updating LLM provider.
/// </summary>
public class LlmProviderCreateDto
{
    public string ProviderType { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }
    public string? ModelId { get; set; }
    public int Priority { get; set; } = 1;
    public int MaxTokens { get; set; } = 4096;
    public decimal CostPerInputToken { get; set; }
    public decimal CostPerOutputToken { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// LLM usage statistics.
/// </summary>
public class LlmUsageStatsDto
{
    public int TotalRequests { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public decimal TotalCost { get; set; }
    public double AverageLatencyMs { get; set; }
    public Dictionary<string, ProviderUsageDto> ByProvider { get; set; } = new();
}

/// <summary>
/// Per-provider usage stats.
/// </summary>
public class ProviderUsageDto
{
    public int RequestCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
}
