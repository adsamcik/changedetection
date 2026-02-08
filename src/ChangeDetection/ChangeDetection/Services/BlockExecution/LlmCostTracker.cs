using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// LiteDB-backed implementation of <see cref="ILlmCostTracker"/>.
/// Uses the existing <see cref="LlmUsageRecord"/> entity for storage.
/// </summary>
public class LlmCostTracker(
    IRepository<LlmUsageRecord> usageRepo,
    ILogger<LlmCostTracker> logger) : ILlmCostTracker
{
    public async Task RecordUsageAsync(Guid watchId, string blockInstanceId, string modelName,
        int inputTokens, int outputTokens, decimal estimatedCost, CancellationToken ct = default)
    {
        var record = new LlmUsageRecord
        {
            WatchedSiteId = watchId,
            ProviderName = modelName,
            Model = modelName,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = estimatedCost,
            IsSuccess = true,
            UsageType = LlmUsageType.Other,
            Timestamp = DateTime.UtcNow
        };

        await usageRepo.InsertAsync(record, ct);
        logger.LogDebug("Recorded LLM usage for watch {WatchId}: {Cost:C} ({InputTokens}+{OutputTokens} tokens)",
            watchId, estimatedCost, inputTokens, outputTokens);
    }

    public async Task<decimal> GetCurrentMonthCostAsync(Guid watchId, CancellationToken ct = default)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var records = await usageRepo.FindAsync(
            r => r.WatchedSiteId == watchId && r.Timestamp >= startOfMonth, ct);

        return records.Sum(r => r.Cost);
    }

    public async Task<bool> IsBudgetExceededAsync(Guid watchId, decimal monthlyBudget, CancellationToken ct = default)
    {
        var currentCost = await GetCurrentMonthCostAsync(watchId, ct);
        return currentCost > monthlyBudget;
    }

    public async Task<LlmUsageSummary> GetUsageSummaryAsync(Guid watchId, CancellationToken ct = default)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var currentMonthRecords = await usageRepo.FindAsync(
            r => r.WatchedSiteId == watchId && r.Timestamp >= startOfMonth, ct);

        var recordsList = currentMonthRecords.ToList();
        var currentMonthCost = recordsList.Sum(r => r.Cost);
        var currentMonthTokens = recordsList.Sum(r => r.InputTokens + r.OutputTokens);

        var totalRecords = await usageRepo.CountAsync(
            r => r.WatchedSiteId == watchId, ct);

        return new LlmUsageSummary(
            WatchId: watchId,
            CurrentMonthCost: currentMonthCost,
            CurrentMonthTokens: currentMonthTokens,
            TotalRecords: totalRecords,
            MonthlyBudget: null);
    }
}
