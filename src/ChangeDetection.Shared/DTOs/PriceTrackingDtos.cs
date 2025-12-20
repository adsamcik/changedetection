namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for price history entries.
/// </summary>
public class PriceHistoryEntryDto
{
    public string Id { get; set; } = "";
    public string WatchId { get; set; } = "";
    public string FieldName { get; set; } = "";
    public decimal Value { get; set; }
    public string? Currency { get; set; }
    public string? StockStatus { get; set; }
    public string? RawPriceText { get; set; }
    public string? RawStockText { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// DTO for price history with chart data.
/// </summary>
public class PriceHistoryResponseDto
{
    /// <summary>
    /// Recent price history entries (most recent first).
    /// </summary>
    public List<PriceHistoryEntryDto> Entries { get; set; } = [];
    
    /// <summary>
    /// Aggregated chart data points for visualization.
    /// </summary>
    public List<ChartDataPointDto> ChartData { get; set; } = [];
    
    /// <summary>
    /// Current price.
    /// </summary>
    public decimal? CurrentPrice { get; set; }
    
    /// <summary>
    /// Currency code.
    /// </summary>
    public string? Currency { get; set; }
    
    /// <summary>
    /// All-time minimum price.
    /// </summary>
    public decimal? MinPrice { get; set; }
    
    /// <summary>
    /// When the minimum price was observed.
    /// </summary>
    public DateTime? MinPriceAt { get; set; }
    
    /// <summary>
    /// All-time maximum price.
    /// </summary>
    public decimal? MaxPrice { get; set; }
    
    /// <summary>
    /// When the maximum price was observed.
    /// </summary>
    public DateTime? MaxPriceAt { get; set; }
    
    /// <summary>
    /// Average price over the time period.
    /// </summary>
    public decimal? AveragePrice { get; set; }
    
    /// <summary>
    /// Current stock status.
    /// </summary>
    public string? CurrentStockStatus { get; set; }
    
    /// <summary>
    /// Total number of price history entries.
    /// </summary>
    public int TotalEntries { get; set; }
}

/// <summary>
/// DTO for chart data points.
/// </summary>
public class ChartDataPointDto
{
    public DateTime Timestamp { get; set; }
    public decimal Value { get; set; }
    public string? Label { get; set; }
}

/// <summary>
/// DTO for alert threshold configuration.
/// </summary>
public class AlertThresholdDto
{
    public string Id { get; set; } = "";
    
    /// <summary>
    /// Type of condition (LessThan, GreaterThan, PercentageDropFromBaseline, etc.)
    /// </summary>
    public string ConditionType { get; set; } = "";
    
    /// <summary>
    /// Threshold value.
    /// </summary>
    public double ThresholdValue { get; set; }
    
    /// <summary>
    /// Whether this threshold is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>
    /// Whether this threshold triggers only once per direction change.
    /// </summary>
    public bool TriggerOnceOnly { get; set; }
    
    /// <summary>
    /// Has this threshold already been triggered (for TriggerOnceOnly thresholds).
    /// </summary>
    public bool HasTriggered { get; set; }
    
    /// <summary>
    /// Minimum time between alerts for this threshold.
    /// </summary>
    public string? CooldownPeriod { get; set; }
    
    /// <summary>
    /// When this threshold was last triggered.
    /// </summary>
    public DateTime? LastTriggeredAt { get; set; }
    
    /// <summary>
    /// Optional notification template override for this threshold.
    /// </summary>
    public string? NotificationTemplateId { get; set; }
}

/// <summary>
/// DTO for creating/updating an alert threshold.
/// </summary>
public class AlertThresholdCreateDto
{
    /// <summary>
    /// Type of condition.
    /// Valid values: LessThan, GreaterThan, LessThanOrEqual, GreaterThanOrEqual,
    /// PercentageDropFromBaseline, PercentageRiseFromBaseline, PercentageDropFromPrevious,
    /// PercentageRiseFromPrevious, AbsoluteDropFromPrevious, AbsoluteRiseFromPrevious,
    /// BackInStock, OutOfStock
    /// </summary>
    public string ConditionType { get; set; } = "";
    
    /// <summary>
    /// Threshold value.
    /// </summary>
    public double ThresholdValue { get; set; }
    
    /// <summary>
    /// Whether this threshold is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>
    /// Whether this threshold triggers only once per direction change.
    /// </summary>
    public bool TriggerOnceOnly { get; set; }
    
    /// <summary>
    /// Minimum time between alerts for this threshold (ISO 8601 duration format).
    /// </summary>
    public string? CooldownPeriod { get; set; }
    
    /// <summary>
    /// Optional notification template override for this threshold.
    /// </summary>
    public string? NotificationTemplateId { get; set; }
}

/// <summary>
/// DTO for price check result returned after manual trigger.
/// </summary>
public class PriceCheckResultDto
{
    public string WatchId { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? PreviousPrice { get; set; }
    public string? Currency { get; set; }
    public string? CurrentStockStatus { get; set; }
    public string? PreviousStockStatus { get; set; }
    public bool HasPriceChange { get; set; }
    public bool HasStockChange { get; set; }
    public double? ChangePercent { get; set; }
    public decimal? ChangeAbsolute { get; set; }
    
    /// <summary>
    /// Whether any alerts were triggered by this price check.
    /// </summary>
    public bool AlertsTriggered { get; set; }
    
    /// <summary>
    /// Names of triggered alerts, if any.
    /// </summary>
    public List<string> TriggeredAlertNames { get; set; } = [];
}
