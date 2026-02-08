namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Tracks per-watch LLM token usage and estimated cost for budget enforcement.
/// </summary>
public interface ILlmCostTracker
{
    /// <summary>Record usage from an LLM block execution.</summary>
    Task RecordUsageAsync(Guid watchId, string blockInstanceId, string modelName,
        int inputTokens, int outputTokens, decimal estimatedCost, CancellationToken ct = default);

    /// <summary>Get total cost for a watch in the current billing period (month).</summary>
    Task<decimal> GetCurrentMonthCostAsync(Guid watchId, CancellationToken ct = default);

    /// <summary>Check if a watch has exceeded its monthly budget.</summary>
    Task<bool> IsBudgetExceededAsync(Guid watchId, decimal monthlyBudget, CancellationToken ct = default);

    /// <summary>Get usage summary for a watch.</summary>
    Task<LlmUsageSummary> GetUsageSummaryAsync(Guid watchId, CancellationToken ct = default);
}

/// <summary>
/// Aggregated LLM usage statistics for a watch.
/// </summary>
public record LlmUsageSummary(
    Guid WatchId,
    decimal CurrentMonthCost,
    int CurrentMonthTokens,
    int TotalRecords,
    decimal? MonthlyBudget);
