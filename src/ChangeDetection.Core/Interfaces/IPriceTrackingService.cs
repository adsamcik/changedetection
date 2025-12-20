using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for extracting, storing, and evaluating price/stock data.
/// </summary>
public interface IPriceTrackingService
{
    /// <summary>
    /// Processes a price check for a watch, storing history and evaluating alerts.
    /// </summary>
    Task<PriceCheckResult> ProcessPriceCheckAsync(
        WatchedSite watch,
        string html,
        CancellationToken ct = default);
}

/// <summary>
/// Result of processing a price check.
/// </summary>
public class PriceCheckResult
{
    public Guid WatchId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? PreviousPrice { get; set; }
    public string? Currency { get; set; }
    public StockStatus? CurrentStockStatus { get; set; }
    public StockStatus? PreviousStockStatus { get; set; }
    public string? RawPriceText { get; set; }
    public string? RawStockText { get; set; }
    public Guid? HistoryEntryId { get; set; }
    public AlertEvaluationResult? AlertResult { get; set; }

    public bool HasPriceChange => PreviousPrice.HasValue && CurrentPrice.HasValue && PreviousPrice != CurrentPrice;
    public bool HasStockChange => PreviousStockStatus.HasValue && CurrentStockStatus.HasValue && PreviousStockStatus != CurrentStockStatus;

    public double? ChangePercent => HasPriceChange && PreviousPrice != 0
        ? (double)((CurrentPrice!.Value - PreviousPrice!.Value) / PreviousPrice.Value * 100)
        : null;

    public decimal? ChangeAbsolute => HasPriceChange
        ? CurrentPrice!.Value - PreviousPrice!.Value
        : null;
}
